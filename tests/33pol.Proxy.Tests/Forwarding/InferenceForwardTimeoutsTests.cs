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

    /// <summary>
    /// An SSE upstream returns headers before it has scheduled the request, so the wait for the
    /// first token is the wait the header allowance was sized for. The first byte gets what the
    /// header phase left of it.
    /// </summary>
    [Fact]
    public void FirstByteTimeout_IsTheRemainderOfTheHeaderAllowance()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60);

        timeouts.FirstByteTimeout(TimeSpan.FromSeconds(20)).Should().Be(TimeSpan.FromSeconds(280));
    }

    /// <summary>The prompt-scaled widening carries over: a long-context request waits longer for its first token too.</summary>
    [Fact]
    public void FirstByteTimeout_FollowsThePromptScaledAllowance()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60).ForRequestBody(10 * 1024 * 1024);

        timeouts.FirstByteTimeout(TimeSpan.Zero).Should().Be(TimeSpan.FromSeconds(900));
    }

    /// <summary>Never shorter than the idle gap, which is the floor for any wait on the body.</summary>
    [Theory]
    [InlineData(290)]
    [InlineData(300)]
    [InlineData(10_000)]
    public void FirstByteTimeout_NeverFallsBelowTheIdleGap(int headerPhaseSeconds)
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60);

        timeouts.FirstByteTimeout(TimeSpan.FromSeconds(headerPhaseSeconds)).Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void FirstByteTimeout_NegativeElapsed_IsTreatedAsZero()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60);

        timeouts.FirstByteTimeout(TimeSpan.FromSeconds(-5)).Should().Be(TimeSpan.FromSeconds(300));
    }

    /// <summary>
    /// Exactly at the boundary the remainder equals the idle gap, so either rule gives the same
    /// answer; one tick either side must still resolve to the larger of the two.
    /// </summary>
    [Fact]
    public void FirstByteTimeout_AtTheBoundary_ResolvesToTheLargerAllowance()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60);

        // 300s header allowance, 120s idle gap: the remainder equals the gap at 180s elapsed.
        timeouts.FirstByteTimeout(TimeSpan.FromSeconds(180)).Should().Be(TimeSpan.FromSeconds(120));
        timeouts.FirstByteTimeout(TimeSpan.FromSeconds(180) - TimeSpan.FromTicks(1))
            .Should().Be(TimeSpan.FromSeconds(120) + TimeSpan.FromTicks(1));
        timeouts.FirstByteTimeout(TimeSpan.FromSeconds(180) + TimeSpan.FromTicks(1))
            .Should().Be(TimeSpan.FromSeconds(120));
    }

    /// <summary>
    /// The write bound is independent of the read gap, and a value the caller never set still bounds
    /// the write rather than leaving it open — the two-argument constructor is what the forwarder
    /// tests and any other caller use.
    /// </summary>
    [Fact]
    public void EffectiveDownstreamWriteTimeout_DefaultsToTheIdleGap()
    {
        new InferenceForwardTimeouts(TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(120))
            .EffectiveDownstreamWriteTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void EffectiveDownstreamWriteTimeout_UsesTheConfiguredValueWhenSet()
    {
        Create(forwardTimeoutSeconds: 300, perMegabyte: 60, downstreamWriteSeconds: 45)
            .EffectiveDownstreamWriteTimeout.Should().Be(TimeSpan.FromSeconds(45));
    }

    /// <summary>Widening the header allowance must not move the write bound.</summary>
    [Fact]
    public void ForRequestBody_LeavesTheDownstreamWriteBoundAlone()
    {
        var timeouts = Create(forwardTimeoutSeconds: 300, perMegabyte: 60, downstreamWriteSeconds: 45);

        timeouts.ForRequestBody(20 * 1024 * 1024).EffectiveDownstreamWriteTimeout
            .Should().Be(TimeSpan.FromSeconds(45));
    }

    private static InferenceForwardTimeouts Create(
        int forwardTimeoutSeconds,
        int perMegabyte,
        int maxSeconds = 3600,
        int downstreamWriteSeconds = 120) =>
        InferenceForwardTimeouts.FromResilience(new GatewayResilienceOptions
        {
            ForwardTimeoutSeconds = forwardTimeoutSeconds,
            ForwardTimeoutSecondsPerRequestMegabyte = perMegabyte,
            MaxForwardTimeoutSeconds = maxSeconds,
            StreamIdleTimeoutSeconds = 120,
            DownstreamWriteTimeoutSeconds = downstreamWriteSeconds,
        });
}
