using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

/// <summary>
/// The load-aware half: what moves the model factor, how far it is allowed to move, and the backoff
/// applied to a caller that keeps being refused.
/// </summary>
public sealed class AdaptiveRateLimitGovernorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The single most important property: adaptation can only ever enforce more strictly than the
    /// configured tier. Nothing here may hand a caller more than the operator allowed.
    /// </summary>
    [Fact]
    public void Evaluate_UnderSustainedSaturation_NeverGoesBelowTheFloorOrAboveOne()
    {
        var bulkheads = new StubBulkheads(new BulkheadModelState("gpt-4", InFlight: 10, Queued: 0, MaxConcurrent: 10, MaxQueued: 10));
        var governor = Create(bulkheads, minFactor: 0.25);

        for (var i = 0; i < 100; i++)
        {
            governor.Evaluate(Now.AddSeconds(i));
        }

        governor.GetModelFactor("gpt-4").Should().Be(0.25);

        // Pressure gone: it recovers, and stops at exactly 1.0.
        bulkheads.States = [new BulkheadModelState("gpt-4", 0, 0, 10, 10)];
        for (var i = 0; i < 100; i++)
        {
            governor.Evaluate(Now.AddSeconds(200 + i));
        }

        governor.GetModelFactor("gpt-4").Should().Be(1.0);
    }

    /// <summary>
    /// Between the watermarks nothing moves. Without the hold band a model hovering at a threshold
    /// would be adjusted on every tick, and the limit a client sees would never settle.
    /// </summary>
    [Fact]
    public void Evaluate_InsideTheHoldBand_LeavesTheFactorAlone()
    {
        var bulkheads = new StubBulkheads(new BulkheadModelState("gpt-4", 10, 0, 10, 10));
        var governor = Create(bulkheads);

        governor.Evaluate(Now);
        var afterFirstCut = governor.GetModelFactor("gpt-4");
        afterFirstCut.Should().BeLessThan(1.0);

        // 70%: above the 0.5 low watermark, below the 0.85 high one.
        bulkheads.States = [new BulkheadModelState("gpt-4", 7, 0, 10, 10)];
        for (var i = 0; i < 20; i++)
        {
            governor.Evaluate(Now.AddSeconds(i + 1));
        }

        governor.GetModelFactor("gpt-4").Should().Be(afterFirstCut);
    }

    /// <summary>A queue filling up is saturation even while slots are free — it is what fills next.</summary>
    [Fact]
    public void Evaluate_WhenOnlyTheQueueIsFull_StillCutsTheFactor()
    {
        var governor = Create(new StubBulkheads(new BulkheadModelState("gpt-4", 1, 10, 10, 10)));

        governor.Evaluate(Now);

        governor.GetModelFactor("gpt-4").Should().BeLessThan(1.0);
    }

    /// <summary>
    /// An open breaker means the upstream is already failing, so the model is treated as fully
    /// saturated whatever its occupancy says.
    /// </summary>
    [Fact]
    public void Evaluate_WithAnOpenCircuitBreaker_CutsEvenAtIdleOccupancy()
    {
        var governor = Create(
            new StubBulkheads(new BulkheadModelState("gpt-4", 0, 0, 10, 10)),
            new StubBreakers(new CircuitBreakerModelState("gpt-4", State: 2)));

        governor.Evaluate(Now);

        governor.GetModelFactor("gpt-4").Should().BeLessThan(1.0);
        governor.Snapshot().Models.Single().Reason.Should().Contain("circuit breaker");
    }

    /// <summary>
    /// Switching adaptation off must restore the configured limits at once. Freezing the factors
    /// would leave every model pinned at its last reduction with nothing left to lift it.
    /// </summary>
    [Fact]
    public void Evaluate_WhenAdaptationIsSwitchedOff_ResetsEveryFactor()
    {
        var config = new MutableConfigProvider(Snapshot(adaptiveEnabled: true));
        var governor = Create(
            new StubBulkheads(new BulkheadModelState("gpt-4", 10, 0, 10, 10)),
            configProvider: config);

        governor.Evaluate(Now);
        governor.GetModelFactor("gpt-4").Should().BeLessThan(1.0);

        config.Current = Snapshot(adaptiveEnabled: false);
        governor.Evaluate(Now.AddSeconds(1));

        governor.GetModelFactor("gpt-4").Should().Be(1.0);
        governor.Snapshot().Models.Should().BeEmpty();
    }

    /// <summary>
    /// An ordinary burst that briefly overruns its tier is told to wait the usual second; only
    /// persistent refusal escalates.
    /// </summary>
    [Fact]
    public void GetRetryAfterSeconds_BelowTheThreshold_IsUnchanged()
    {
        var governor = Create(new StubBulkheads());

        for (var i = 0; i < 4; i++)
        {
            governor.RecordOutcome("t:acme", admitted: false, Now);
        }

        governor.GetRetryAfterSeconds("t:acme", 1, Now).Should().Be(1);
    }

    [Fact]
    public void GetRetryAfterSeconds_PastTheThreshold_EscalatesAndIsCapped()
    {
        var governor = Create(new StubBulkheads());

        for (var i = 0; i < 200; i++)
        {
            governor.RecordOutcome("t:acme", admitted: false, Now);
        }

        var retryAfter = governor.GetRetryAfterSeconds("t:acme", 1, Now);

        retryAfter.Should().BeGreaterThan(1);
        retryAfter.Should().BeLessThanOrEqualTo(60, "clients sleep for this header; unbounded is a hang");
    }

    /// <summary>One success clears the penalty: a bursty-but-legitimate client must never be held down.</summary>
    [Fact]
    public void RecordOutcome_AnAdmittedRequest_ClearsTheBackoff()
    {
        var governor = Create(new StubBulkheads());

        for (var i = 0; i < 200; i++)
        {
            governor.RecordOutcome("t:acme", admitted: false, Now);
        }

        governor.RecordOutcome("t:acme", admitted: true, Now);

        governor.GetRetryAfterSeconds("t:acme", 1, Now).Should().Be(1);
        governor.Snapshot().BackedOffPartitions.Should().Be(0);
    }

    /// <summary>
    /// Jitter spreads a refused crowd out, but never below what the bucket said — telling a client
    /// to come back before a token exists just buys another rejection.
    /// </summary>
    [Fact]
    public void GetRetryAfterSeconds_NeverReturnsLessThanTheBucketAsked()
    {
        var governor = Create(new StubBulkheads());

        for (var i = 0; i < 500; i++)
        {
            governor.RecordOutcome("t:acme", admitted: false, Now);
            governor.GetRetryAfterSeconds("t:acme", 7, Now).Should().BeGreaterThanOrEqualTo(7);
        }
    }

    /// <summary>With adaptation off, every lever is inert and the configured tier is what is enforced.</summary>
    [Fact]
    public void WhenDisabled_EveryLeverIsInert()
    {
        var governor = Create(
            new StubBulkheads(new BulkheadModelState("gpt-4", 10, 10, 10, 10)),
            enabled: false);

        governor.Evaluate(Now);
        for (var i = 0; i < 100; i++)
        {
            governor.RecordOutcome("t:acme", admitted: false, Now);
        }

        governor.IsEnabled.Should().BeFalse();
        governor.GetModelFactor("gpt-4").Should().Be(1.0);
        governor.GetRetryAfterSeconds("t:acme", 3, Now).Should().Be(3);
        governor.Snapshot().Should().BeSameAs(AdaptiveRateLimitSnapshot.Disabled);
    }

    /// <summary>
    /// Nonsense gains — a high watermark under the low one, a decrease factor of 1 — are clamped
    /// into a range the control law is stable in, so a typo in appsettings degrades rather than
    /// becoming an incident.
    /// </summary>
    [Fact]
    public void Construction_WithInvertedWatermarks_StillConverges()
    {
        var governor = Create(
            new StubBulkheads(new BulkheadModelState("gpt-4", 10, 0, 10, 10)),
            configure: o =>
            {
                o.HighWatermark = 0.1;
                o.LowWatermark = 0.9;
                o.DecreaseFactor = 5.0;
                o.MinFactor = 3.0;
            });

        for (var i = 0; i < 50; i++)
        {
            governor.Evaluate(Now.AddSeconds(i));
        }

        governor.GetModelFactor("gpt-4").Should().BeInRange(0.0, 1.0);
    }


    /// <summary>
    /// The backoff table is bounded. Every refused partition takes an entry, and anonymous traffic
    /// partitions by client address block, so an unbounded table grows with the size of a rejected
    /// flood — the same failure the store's own partition table is capped against, in a table that
    /// had no cap at all and was only trimmed by a timer minutes later.
    /// </summary>
    [Fact]
    public void RecordOutcome_PastTheCeiling_StopsTrackingNewPartitions()
    {
        var governor = Create(new StubBulkheads());

        for (var i = 0; i < 20_500; i++)
        {
            governor.RecordOutcome($"anon:198.51.100.{i}", admitted: false, Now);
        }

        governor.Snapshot().BackedOffPartitions.Should().Be(20_000);
    }

    /// <summary>
    /// The ceiling must not cost the partitions it exists to catch. A partition already tracked keeps
    /// escalating however full the table is — otherwise a flood of one-off sources would be a way to
    /// clear the penalty on the repeat offender.
    /// </summary>
    [Fact]
    public void RecordOutcome_PastTheCeiling_StillEscalatesAnAlreadyTrackedPartition()
    {
        var governor = Create(new StubBulkheads(), configure: options =>
        {
            options.BackoffAfterConsecutiveRejections = 3;
            options.RetryAfterJitter = 0;
        });

        for (var i = 0; i < 10; i++)
        {
            governor.RecordOutcome("repeat-offender", admitted: false, Now);
        }

        for (var i = 0; i < 25_000; i++)
        {
            governor.RecordOutcome($"anon:198.51.100.{i}", admitted: false, Now);
        }

        governor.RecordOutcome("repeat-offender", admitted: false, Now);

        governor.GetRetryAfterSeconds("repeat-offender", 1, Now).Should()
            .BeGreaterThan(1, "the offender kept its escalation while the newcomers went untracked");
    }

    /// <summary>An admitted request releases its entry, so the table shrinks as clients recover.</summary>
    [Fact]
    public void RecordOutcome_WhenAPartitionRecovers_ReleasesItsSlot()
    {
        var governor = Create(new StubBulkheads());

        governor.RecordOutcome("tenant-a", admitted: false, Now);
        governor.RecordOutcome("tenant-b", admitted: false, Now);
        governor.Snapshot().BackedOffPartitions.Should().Be(2);

        governor.RecordOutcome("tenant-a", admitted: true, Now);

        governor.Snapshot().BackedOffPartitions.Should().Be(1);
    }

    /// <summary>
    /// The table stays at its ceiling under a flood, and the maintenance tick does not cost a
    /// tracked partition its escalation.
    /// </summary>
    /// <remarks>
    /// The request path refuses to add past the ceiling, so the trim in <c>Evaluate</c> is the
    /// backstop for the one way the count can still overshoot: two threads passing the check at the
    /// same instant. It drops the partitions refused longest ago, which is why a partition still
    /// being refused — touched on every refusal — cannot be dropped and handed a fresh,
    /// un-escalated <c>Retry-After</c> by a flood of newcomers.
    /// </remarks>
    [Fact]
    public void Evaluate_UnderAFloodOfNewPartitions_HoldsTheCeilingAndKeepsEscalations()
    {
        var governor = Create(new StubBulkheads(), configure: options =>
        {
            options.BackoffAfterConsecutiveRejections = 1;
            options.RetryAfterJitter = 0;
            // Long enough that nothing expires on age during this test.
            options.MaxRetryAfterSeconds = 3600;
        });

        for (var i = 0; i < 20_000; i++)
        {
            governor.RecordOutcome($"old:{i}", admitted: false, Now);
        }

        // Room is only made by expiry or the trim, so these are refused but untracked...
        for (var i = 0; i < 100; i++)
        {
            governor.RecordOutcome($"new:{i}", admitted: false, Now.AddSeconds(30));
        }

        governor.Snapshot().BackedOffPartitions.Should().Be(20_000);

        // ...while one already-tracked partition keeps being refused, so it stays the most recently
        // touched entry and sorts out of reach of any trim.
        governor.RecordOutcome("old:0", admitted: false, Now.AddSeconds(45));
        governor.RecordOutcome("old:0", admitted: false, Now.AddSeconds(46));

        governor.Evaluate(Now.AddSeconds(60));

        governor.Snapshot().BackedOffPartitions.Should().BeLessThanOrEqualTo(20_000);
        governor.GetRetryAfterSeconds("old:0", 1, Now.AddSeconds(60)).Should()
            .BeGreaterThan(1, "a tracked partition keeps its escalation across a trim that had nothing to drop");
    }

    private static AdaptiveRateLimitGovernor Create(
        IBulkheadStateSource bulkheads,
        ICircuitBreakerStateSource? breakers = null,
        double minFactor = 0.25,
        bool enabled = true,
        IGatewayConfigProvider? configProvider = null,
        Action<AdaptiveRateLimitOptions>? configure = null)
    {
        var adaptive = new AdaptiveRateLimitOptions { Enabled = enabled, MinFactor = minFactor };
        configure?.Invoke(adaptive);

        return new AdaptiveRateLimitGovernor(
            configProvider ?? new MutableConfigProvider(Snapshot(adaptiveEnabled: enabled)),
            NullLogger<AdaptiveRateLimitGovernor>.Instance,
            Options.Create(new RateLimitingOptions { Adaptive = adaptive }),
            bulkheads,
            breakers);
    }

    private static GatewayConfigSnapshot Snapshot(bool adaptiveEnabled) =>
        new() { RateLimits = new RateLimitsConfigSection { AdaptiveEnabled = adaptiveEnabled } };

    private sealed class StubBulkheads(params BulkheadModelState[] states) : IBulkheadStateSource
    {
        public IReadOnlyList<BulkheadModelState> States { get; set; } = states;

        public IReadOnlyList<BulkheadModelState> GetStates() => States;
    }

    private sealed class StubBreakers(params CircuitBreakerModelState[] states) : ICircuitBreakerStateSource
    {
        public IReadOnlyList<CircuitBreakerModelState> GetStates() => states;
    }

    private sealed class MutableConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; set; } = snapshot;
    }
}
