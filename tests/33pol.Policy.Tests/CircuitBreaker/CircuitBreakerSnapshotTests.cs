using Breaker = Pol33.Policy.CircuitBreaker.CircuitBreaker;
using Pol33.Policy.CircuitBreaker;

namespace Pol33.Policy.Tests.CircuitBreaker;

public sealed class CircuitBreakerSnapshotTests
{
    private static readonly CircuitBreakerPolicyOptions Policy = new()
    {
        FailureThreshold = 3,
        BreakDuration = TimeSpan.FromMinutes(1),
    };

    [Fact]
    public void GetSnapshot_Closed_ReportsWindowCountsAndNoOpenedAt()
    {
        var sut = new Breaker(Policy);
        sut.RecordFailure();
        sut.RecordSuccess();

        var snapshot = sut.GetSnapshot();

        snapshot.State.Should().Be(CircuitState.Closed);
        snapshot.OpenedAt.Should().BeNull();
        snapshot.FailuresInWindow.Should().Be(1);
        snapshot.OutcomesInWindow.Should().Be(2);
        snapshot.RemainingBreak.Should().BeNull();
    }

    [Fact]
    public void GetSnapshot_WhenOpen_ReportsOpenedAtAndRemainingBreak()
    {
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var clock = now;
        var sut = new Breaker(Policy, () => clock);
        for (var i = 0; i < 3; i++)
        {
            sut.TryEnter();
            sut.RecordFailure();
        }

        clock = now.AddSeconds(20);
        var snapshot = sut.GetSnapshot();

        snapshot.State.Should().Be(CircuitState.Open);
        snapshot.OpenedAt.Should().Be(now);
        snapshot.RemainingBreak.Should().Be(TimeSpan.FromSeconds(40));
        snapshot.FailuresInWindow.Should().Be(0, "the window is cleared when the breaker trips");
    }
}
