using Pol33.App.DependencyInjection;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Integration.Tests.Configuration;

/// <summary>
/// The config state projects scheduled windows lazily: the snapshot it hands out changes the moment
/// a window boundary has passed, with a new effective version, and nothing else has to run.
/// </summary>
public sealed class GatewayConfigStateScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 19, 0, 0, TimeSpan.Zero);

    private static GatewayConfigSnapshot Scheduled(DateTimeOffset from, DateTimeOffset until) => new()
    {
        RateLimits = new RateLimitsConfigSection
        {
            Models = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase) { ["gpt-4"] = new(600, 60, 40) },
            Schedules = new Dictionary<string, IReadOnlyList<RateLimitWindowDefinition>>(StringComparer.OrdinalIgnoreCase)
            {
                ["model:gpt-4"] = [new RateLimitWindowDefinition("launch", RateLimitWindowKinds.Once, 3000, 500, 120, From: from, Until: until)],
            },
        },
    };

    [Fact]
    public void Current_CrossesAWindowStartWithoutAnyExternalTrigger()
    {
        var clock = new ManualClock(T0);
        var state = new GatewayConfigState(Scheduled(T0.AddMinutes(30), T0.AddHours(2)), [], clock);

        state.Current.RateLimits.Models["gpt-4"].Rpm.Should().Be(600);
        var beforeVersion = state.Current.RateLimits.EffectiveVersion;

        clock.Now = T0.AddMinutes(31);

        state.Current.RateLimits.Models["gpt-4"].Rpm.Should().Be(3000);
        state.Current.RateLimits.EffectiveVersion.Should().BeGreaterThan(beforeVersion, "the plan cache must miss");
        state.Current.RateLimits.StoredOrSelf.Models["gpt-4"].Rpm.Should().Be(600);

        clock.Now = T0.AddHours(3);

        state.Current.RateLimits.Models["gpt-4"].Rpm.Should().Be(600, "the window ended");
    }

    [Fact]
    public void Current_WithoutSchedules_IsTheStoredSnapshot()
    {
        var clock = new ManualClock(T0);
        var state = new GatewayConfigState(GatewayConfigSnapshot.Defaults, [], clock);

        state.Current.Should().BeSameAs(state.Stored);
        state.Current.RateLimits.EffectiveVersion.Should().Be(0);
    }

    [Fact]
    public void Set_ReprojectsImmediately()
    {
        var clock = new ManualClock(T0);
        var state = new GatewayConfigState(GatewayConfigSnapshot.Defaults, [], clock);

        state.Set(Scheduled(T0.AddHours(-1), T0.AddHours(1)));

        state.Current.RateLimits.Models["gpt-4"].Rpm.Should().Be(3000);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
