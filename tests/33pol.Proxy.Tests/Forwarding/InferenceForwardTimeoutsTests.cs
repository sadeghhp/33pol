using Pol33.Core.Configuration;
using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.Forwarding;

/// <summary>
/// Time to first response byte scales with the prompt, because the backend reads and pre-fills the
/// whole context before it can answer. A flat allowance therefore expired on long-context requests
/// purely because they were long, and the circuit breaker counted each expiry against a backend that
/// was working correctly.
/// </summary>
public sealed class InferenceForwardTimeoutsTests
{
    [Fact]
    public void ForRequestBody_SmallRequest_KeepsTheBaseAllowance()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60);

        timeouts.ForRequestBody(4_096).HeaderTimeout.Should().Be(TimeSpan.FromSeconds(360));
    }

    [Fact]
    public void ForRequestBody_NoBody_IsUnchanged()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60);

        timeouts.ForRequestBody(0).HeaderTimeout.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void ForRequestBody_LargeContextRequest_WidensInProportionToThePrompt()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60);

        // 20 MB of prompt buys 20 more minutes on top of the base allowance.
        timeouts.ForRequestBody(20 * 1024 * 1024).HeaderTimeout
            .Should().Be(TimeSpan.FromSeconds(300 + (20 * 60)));
    }

    [Fact]
    public void ForRequestBody_IsCappedByMaxForwardTimeout()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60, maxSeconds: 600);

        timeouts.ForRequestBody(100 * 1024 * 1024).HeaderTimeout.Should().Be(TimeSpan.FromSeconds(600));
    }

    [Fact]
    public void ForRequestBody_ScalingDisabled_KeepsAFlatAllowance()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 0);

        timeouts.ForRequestBody(20 * 1024 * 1024).HeaderTimeout.Should().Be(TimeSpan.FromSeconds(300));
    }

    /// <summary>The body deadline is independent of the prompt: it governs transfer, not generation.</summary>
    [Fact]
    public void ForRequestBody_LeavesTheIdleDeadlineAlone()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60);

        timeouts.ForRequestBody(20 * 1024 * 1024).StreamIdleTimeout
            .Should().Be(timeouts.StreamIdleTimeout);
    }

    /// <summary>A body far larger than any sane cap must not overflow the allowance arithmetic.</summary>
    [Fact]
    public void ForRequestBody_AbsurdBodySize_StaysWithinTheCap()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60, maxSeconds: 3600);

        timeouts.ForRequestBody(long.MaxValue).HeaderTimeout.Should().Be(TimeSpan.FromSeconds(3600));
    }

    private static InferenceForwardTimeouts Create(
        int forwardTimeoutSeconds,
        int perMegabyte,
        int maxSeconds = 3600) =>
        InferenceForwardTimeouts.FromResilience(new GatewayResilienceOptions
        {
            ForwardTimeoutSeconds = forwardTimeoutSeconds,
            ForwardTimeoutSecondsPerRequestMegabyte = perMegabyte,
            MaxForwardTimeoutSeconds = maxSeconds,
            StreamIdleTimeoutSeconds = 120,
        });
}
