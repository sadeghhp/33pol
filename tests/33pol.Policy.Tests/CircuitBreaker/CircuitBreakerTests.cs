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

    private static void TripOpen(Breaker breaker)
    {
        for (var i = 0; i < DefaultPolicy.FailureThreshold; i++)
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
