using Breaker = Pol33.Policy.CircuitBreaker.CircuitBreaker;
using Pol33.Policy.CircuitBreaker;

namespace Pol33.Policy.Tests.CircuitBreaker;

public sealed class CircuitBreakerTests
{
    private static readonly CircuitBreakerPolicyOptions DefaultPolicy = new()
    {
        FailureThreshold = 3,
        BreakDuration = TimeSpan.FromMinutes(1),
    };

    [Fact]
    public void TryEnter_Closed_AllowsRequests()
    {
        var sut = new Breaker(DefaultPolicy);
        sut.TryEnter().Should().BeTrue();
        sut.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void RecordFailure_AtThreshold_OpensCircuit()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var sut = new Breaker(DefaultPolicy, () => clock.Now);

        for (var i = 0; i < 3; i++)
        {
            sut.TryEnter().Should().BeTrue();
            sut.RecordFailure();
        }

        sut.State.Should().Be(CircuitState.Open);
        sut.TryEnter().Should().BeFalse();
    }

    [Fact]
    public void TryEnter_AfterBreakDuration_TransitionsToHalfOpen()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var sut = new Breaker(DefaultPolicy, () => clock.Now);

        TripOpen(sut);
        clock.Advance(TimeSpan.FromMinutes(2));

        sut.TryEnter().Should().BeTrue();
        sut.State.Should().Be(CircuitState.HalfOpen);
    }

    [Fact]
    public void RecordSuccess_FromHalfOpen_ClosesCircuit()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var sut = new Breaker(DefaultPolicy, () => clock.Now);

        TripOpen(sut);
        clock.Advance(TimeSpan.FromMinutes(2));
        sut.TryEnter().Should().BeTrue();

        sut.RecordSuccess();
        sut.State.Should().Be(CircuitState.Closed);
        sut.TryEnter().Should().BeTrue();
    }

    [Fact]
    public void RecordFailure_FromHalfOpen_ReopensCircuit()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var sut = new Breaker(DefaultPolicy, () => clock.Now);

        TripOpen(sut);
        clock.Advance(TimeSpan.FromMinutes(2));
        sut.TryEnter().Should().BeTrue();

        sut.RecordFailure();
        sut.State.Should().Be(CircuitState.Open);
        sut.TryEnter().Should().BeFalse();
    }

    [Fact]
    public void TryEnter_WhileHalfOpenProbeOutstanding_AdmitsOnlyOneRequest()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var sut = new Breaker(DefaultPolicy, () => clock.Now);

        TripOpen(sut);
        clock.Advance(TimeSpan.FromMinutes(2));

        sut.TryEnter().Should().BeTrue();
        sut.TryEnter().Should().BeFalse();
        sut.TryEnter().Should().BeFalse();
    }

    [Fact]
    public void RecordAbandoned_AfterHalfOpenProbe_RestoresPermitWithoutChangingState()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var sut = new Breaker(DefaultPolicy, () => clock.Now);

        TripOpen(sut);
        clock.Advance(TimeSpan.FromMinutes(2));
        sut.TryEnter().Should().BeTrue();

        sut.RecordAbandoned();

        // Still probing — abandoning is neither a success nor a failure.
        sut.State.Should().Be(CircuitState.HalfOpen);
        // ...but the next request can probe, rather than being rejected forever.
        sut.TryEnter().Should().BeTrue();
    }

    [Fact]
    public void RecordAbandoned_WhenProbeNeverReportsOutcome_DoesNotWedgeBreakerOpen()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var sut = new Breaker(DefaultPolicy, () => clock.Now);

        TripOpen(sut);
        clock.Advance(TimeSpan.FromMinutes(2));

        // A probe is admitted but ends without a verdict (client abort, gateway-side rejection).
        sut.TryEnter().Should().BeTrue();
        sut.RecordAbandoned();

        // The backend recovers and the next probe succeeds: the circuit must be able to close.
        sut.TryEnter().Should().BeTrue();
        sut.RecordSuccess();

        sut.State.Should().Be(CircuitState.Closed);
        sut.TryEnter().Should().BeTrue();
    }

    [Fact]
    public void RecordAbandoned_WhenClosed_IsNoOp()
    {
        var sut = new Breaker(DefaultPolicy);

        sut.TryEnter().Should().BeTrue();
        sut.RecordAbandoned();

        sut.State.Should().Be(CircuitState.Closed);
        sut.TryEnter().Should().BeTrue();
    }

    /// <summary>
    /// A half-open probe that has not reported an outcome within
    /// <see cref="CircuitBreakerPolicyOptions.HalfOpenProbeTimeout"/> loses its permit to the next
    /// caller.
    /// </summary>
    /// <remarks>
    /// The defect this covers: the permit was held until the probe reported, with no deadline. Here a
    /// probe is a whole inference, so a breaker that tripped during a slow patch refused every other
    /// request for that model for as long as the probe ran — minutes, not the 30s break duration.
    /// A model that was merely slow presented to every caller as one that answered nothing at all.
    /// </remarks>
    [Fact]
    public void TryEnter_HalfOpenProbePastTimeout_ReclaimsPermitForTheNextCaller()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var policy = PolicyWithProbeTimeout(TimeSpan.FromSeconds(30));
        var sut = new Breaker(policy, () => clock.Now);

        TripOpen(sut, policy);
        clock.Advance(TimeSpan.FromMinutes(2));

        sut.TryEnter().Should().BeTrue("the first caller after the break duration is the probe");
        clock.Advance(TimeSpan.FromSeconds(31));

        sut.TryEnter().Should().BeTrue("a probe that has not reported within the timeout is presumed stalled");
        sut.State.Should().Be(CircuitState.HalfOpen, "reclaiming a permit is not an outcome");
    }

    [Fact]
    public void TryEnter_HalfOpenProbeWithinTimeout_StillRefusesOtherCallers()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var policy = PolicyWithProbeTimeout(TimeSpan.FromSeconds(30));
        var sut = new Breaker(policy, () => clock.Now);

        TripOpen(sut, policy);
        clock.Advance(TimeSpan.FromMinutes(2));
        sut.TryEnter().Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(29));

        sut.TryEnter().Should().BeFalse("one probe at a time is still the rule while it is within its deadline");
    }

    /// <summary>
    /// The end-to-end shape of the outage: a long-running probe must not take the model out of
    /// service for its whole duration.
    /// </summary>
    [Fact]
    public void TryEnter_ProbeRunningForMinutes_DoesNotBlockTheModelThroughout()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var policy = PolicyWithProbeTimeout(TimeSpan.FromSeconds(30));
        var sut = new Breaker(policy, () => clock.Now);

        TripOpen(sut, policy);
        clock.Advance(TimeSpan.FromMinutes(2));
        sut.TryEnter().Should().BeTrue("the probe is admitted");

        // The probe is a ten-minute generation and never reports back. Traffic keeps arriving.
        var admitted = 0;
        for (var minute = 0; minute < 10; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            if (sut.TryEnter())
            {
                admitted++;
            }
        }

        admitted.Should().Be(
            10,
            "each minute is past the 30s probe deadline, so traffic resumes trickling through "
            + "instead of the model being refused for the probe's whole duration");
    }

    private static CircuitBreakerPolicyOptions PolicyWithProbeTimeout(TimeSpan probeTimeout) => new()
    {
        FailureThreshold = DefaultPolicy.FailureThreshold,
        BreakDuration = DefaultPolicy.BreakDuration,
        HalfOpenProbeTimeout = probeTimeout,
    };

    private static void TripOpen(Breaker breaker) => TripOpen(breaker, DefaultPolicy);

    private static void TripOpen(Breaker breaker, CircuitBreakerPolicyOptions policy)
    {
        for (var i = 0; i < policy.FailureThreshold; i++)
        {
            breaker.TryEnter().Should().BeTrue();
            breaker.RecordFailure();
        }
    }

    private sealed class MutableClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan delta) => Now = Now.Add(delta);
    }
}
