using Pol33.Core.Configuration;

namespace Pol33.Core.Tests.Configuration;

/// <summary>
/// The reservation TTL is a backstop, not a lifecycle mechanism. It must outlive the longest
/// possible in-flight request plus the usage-flush delay; the shipped default previously did not
/// (120s TTL against a 300s forward timeout), so reservations were swept mid-request and concurrent
/// long requests could each be admitted against the same headroom.
/// </summary>
public sealed class BillingReservationTtlPolicyTests
{
    [Fact]
    public void MinimumTtl_CoversHeaderTimeoutStreamIdleTimeoutAndFlushDelay()
    {
        var resilience = new GatewayResilienceOptions
        {
            ForwardTimeoutSeconds = 300,
            StreamIdleTimeoutSeconds = 120,
        };
        var billing = new BillingOptions { UsageWriterFlushIntervalMs = 2_000 };

        var minimum = BillingReservationTtlPolicy.MinimumTtlSeconds(resilience, billing);

        minimum.Should().Be(300 + 120 + 2 + BillingReservationTtlPolicy.SafetyMarginSeconds);
    }

    /// <summary>
    /// Even a deployment configured with second-scale timeouts keeps a usable backstop: the floor
    /// and the safety margin both hold it above <see cref="BillingReservationTtlPolicy.MinimumSeconds"/>.
    /// </summary>
    [Fact]
    public void MinimumTtl_NeverDropsBelowTheAbsoluteFloor()
    {
        var resilience = new GatewayResilienceOptions
        {
            ForwardTimeoutSeconds = 1,
            StreamIdleTimeoutSeconds = 1,
        };
        var billing = new BillingOptions { UsageWriterFlushIntervalMs = 1 };

        BillingReservationTtlPolicy.MinimumTtlSeconds(resilience, billing)
            .Should().BeGreaterThanOrEqualTo(BillingReservationTtlPolicy.MinimumSeconds);
    }

    /// <summary>Negative or zero timings must not be able to produce a nonsensical minimum.</summary>
    [Fact]
    public void MinimumTtl_ClampsNonPositiveTimings()
    {
        var resilience = new GatewayResilienceOptions
        {
            ForwardTimeoutSeconds = -50,
            StreamIdleTimeoutSeconds = 0,
        };
        var billing = new BillingOptions { UsageWriterFlushIntervalMs = -1 };

        BillingReservationTtlPolicy.MinimumTtlSeconds(resilience, billing)
            .Should().Be(BillingReservationTtlPolicy.MinimumSeconds);
    }

    [Fact]
    public void ShippedDefaults_SatisfyThePolicy()
    {
        var resilience = new GatewayResilienceOptions();
        var billing = new BillingOptions();

        BillingReservationTtlPolicy.IsSufficient(resilience, billing)
            .Should()
            .BeTrue(
                "the default TTL must be safe out of the box; the previous 120s default was shorter " +
                "than the 300s forward timeout");
    }

    [Fact]
    public void TtlShorterThanTheLongestRequest_IsRejected()
    {
        var resilience = new GatewayResilienceOptions
        {
            ForwardTimeoutSeconds = 300,
            StreamIdleTimeoutSeconds = 120,
        };
        var billing = new BillingOptions { BudgetReservationTtlSeconds = 120 };

        BillingReservationTtlPolicy.IsSufficient(resilience, billing).Should().BeFalse();
        BillingReservationTtlPolicy.DescribeInsufficient(resilience, billing)
            .Should().Contain(nameof(BillingOptions.BudgetReservationTtlSeconds));
    }

    [Fact]
    public void RaisingResilienceTimeoutsRaisesTheRequiredTtl()
    {
        var billing = new BillingOptions { BudgetReservationTtlSeconds = 900 };

        var modest = new GatewayResilienceOptions
        {
            ForwardTimeoutSeconds = 300,
            StreamIdleTimeoutSeconds = 120,
        };
        var generous = new GatewayResilienceOptions
        {
            ForwardTimeoutSeconds = 1_800,
            StreamIdleTimeoutSeconds = 600,
        };

        BillingReservationTtlPolicy.IsSufficient(modest, billing).Should().BeTrue();
        BillingReservationTtlPolicy.IsSufficient(generous, billing).Should().BeFalse();
    }
}
