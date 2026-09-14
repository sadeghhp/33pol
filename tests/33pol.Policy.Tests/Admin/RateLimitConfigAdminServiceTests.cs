using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.Admin;

namespace Pol33.Policy.Tests.Admin;

public sealed class RateLimitConfigAdminServiceTests
{
    [Fact]
    public async Task UpdateAsync_ValidPayload_PersistsToRepositoryAndRefreshes()
    {
        var repo = new RecordingRepository();
        var refresher = new RecordingRefresher();
        var service = CreateService(new StubServiceProvider(repo, refresher));

        var result = await service.UpdateAsync(
            enabled: true,
            adaptiveEnabled: false,
            new RateLimitTierOptions { Rpm = 30, Burst = 3, MaxConcurrentStreams = 3 },
            new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["enterprise"] = new() { Rpm = 300, Burst = 30, MaxConcurrentStreams = 30 },
            });

        result.Success.Should().BeTrue();
        repo.SavedDefault!.Rpm.Should().Be(30);
        repo.SavedPlans!["enterprise"].Rpm.Should().Be(300);
        refresher.Refreshed.Should().BeTrue();
    }

    /// <summary>
    /// A rule set is replaced wholesale, so a write based on a stale read does not merge with what
    /// landed in between — it erases it. Two operators with the page open would each save their own
    /// complete set and the second would win silently, including for rules like <c>auth_failure</c>.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_WhenTheConfigurationMovedUnderTheCaller_IsRefusedWith409()
    {
        var repo = new RecordingRepository { CurrentVersion = 7 };
        var service = CreateService(new StubServiceProvider(repo, new RecordingRefresher()));

        var result = await service.UpdateAsync(
            enabled: true,
            adaptiveEnabled: false,
            new RateLimitTierOptions { Rpm = 30, Burst = 3, MaxConcurrentStreams = 3 },
            new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase),
            rules: null,
            expectedVersion: 5);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(409);
        result.Message.Should().Contain("someone else");
        repo.SavedDefault.Should().BeNull("nothing is written when the precondition fails");
    }

    /// <summary>A write based on the current version goes through, and the version is passed on.</summary>
    [Fact]
    public async Task UpdateAsync_WithTheCurrentVersion_Succeeds()
    {
        var repo = new RecordingRepository { CurrentVersion = 7 };
        var service = CreateService(new StubServiceProvider(repo, new RecordingRefresher()));

        var result = await service.UpdateAsync(
            enabled: true,
            adaptiveEnabled: false,
            new RateLimitTierOptions { Rpm = 30, Burst = 3, MaxConcurrentStreams = 3 },
            new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase),
            rules: null,
            expectedVersion: 7);

        result.Success.Should().BeTrue();
        repo.SavedExpectedVersion.Should().Be(7);
    }

    /// <summary>No precondition is an unconditional write, which is what an older client sends.</summary>
    [Fact]
    public async Task UpdateAsync_WithoutAnExpectedVersion_DoesNotCheck()
    {
        var repo = new RecordingRepository { CurrentVersion = 7 };
        var service = CreateService(new StubServiceProvider(repo, new RecordingRefresher()));

        var result = await service.UpdateAsync(
            enabled: true,
            adaptiveEnabled: false,
            new RateLimitTierOptions { Rpm = 30, Burst = 3, MaxConcurrentStreams = 3 },
            new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase));

        result.Success.Should().BeTrue();
        repo.SavedExpectedVersion.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_InvalidRpm_ReturnsValidationError()
    {
        var service = CreateService(new StubServiceProvider(null, null));

        var result = await service.UpdateAsync(
            enabled: true,
            adaptiveEnabled: false,
            new RateLimitTierOptions { Rpm = 0, Burst = 0, MaxConcurrentStreams = 0 },
            new Dictionary<string, RateLimitTierOptions>());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task UpdateAsync_NoDatabaseConfigured_Returns503()
    {
        var service = CreateService(new StubServiceProvider(null, null));

        var result = await service.UpdateAsync(
            enabled: true,
            adaptiveEnabled: false,
            new RateLimitTierOptions { Rpm = 60, Burst = 10, MaxConcurrentStreams = 5 },
            new Dictionary<string, RateLimitTierOptions>());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(503);
    }

    /// <summary>
    /// The anonymous tier round-trips as a singleton rule, like the auth-failure one: present on a
    /// GET when set, and absent when the deployment has none — an absent tier must not come back as
    /// a rule that enforces nothing.
    /// </summary>
    [Fact]
    public void GetCurrent_ListsTheAnonymousTierAsASingletonRule()
    {
        var service = CreateService(
            new StubServiceProvider(null, null),
            new GatewayConfigSnapshot
            {
                RateLimits = new RateLimitsConfigSection
                {
                    AuthFailure = new RateLimitPolicy(60, 20, 0),
                    Anonymous = new RateLimitPolicy(60, 20, 2),
                },
            });

        var rules = service.GetCurrent().Rules;

        rules.Should().Contain(r =>
            r.Scope == RateLimitScopeNames.Anonymous &&
            r.TargetKey == RateLimitScopeNames.SingletonTarget &&
            r.Rpm == 60 && r.Burst == 20 && r.MaxConcurrentStreams == 2);
    }

    [Fact]
    public void GetCurrent_WithNoAnonymousTier_ListsNoAnonymousRule()
    {
        var service = CreateService(new StubServiceProvider(null, null), new GatewayConfigSnapshot());

        service.GetCurrent().Rules.Should().NotContain(r => r.Scope == RateLimitScopeNames.Anonymous);
    }

    private static readonly RateLimitWindowDefinition StoredWindow = new(
        "off-peak", RateLimitWindowKinds.Weekly, 1200, 200, 80,
        Days: ["mon", "tue", "wed", "thu", "fri"], Start: "19:00", End: "07:00", TimeZone: "Europe/Berlin");

    private static GatewayConfigSnapshot SnapshotWithScheduledRule() => new()
    {
        RateLimits = new RateLimitsConfigSection
        {
            Models = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase) { ["gpt-4"] = new(600, 60, 40) },
            Schedules = new Dictionary<string, IReadOnlyList<RateLimitWindowDefinition>>(StringComparer.OrdinalIgnoreCase)
            {
                ["model:gpt-4"] = [StoredWindow],
            },
        },
    };

    [Fact]
    public void GetCurrent_AttachesTheStoredScheduleToItsRule()
    {
        var service = CreateService(new StubServiceProvider(null, null), SnapshotWithScheduledRule());

        var rule = service.GetCurrent().Rules.Single(r => r.TargetKey == "gpt-4");

        rule.Schedule.Should().ContainSingle().Which.Name.Should().Be("off-peak");
        rule.Rpm.Should().Be(600);
    }

    [Fact]
    public void GetCurrent_ReadsBaseTiers_NotTheProjectedOnes()
    {
        var stored = SnapshotWithScheduledRule().RateLimits;
        var (projected, _) = RateLimitScheduleProjection.Project(
            stored,
            new DateTimeOffset(2026, 9, 18, 19, 14, 0, TimeSpan.Zero),
            1);
        var service = CreateService(new StubServiceProvider(null, null), new GatewayConfigSnapshot { RateLimits = projected });

        var rule = service.GetCurrent().Rules.Single(r => r.TargetKey == "gpt-4");

        rule.Rpm.Should().Be(600, "the window is active at that instant but the admin API shows what was configured");
    }

    [Fact]
    public async Task UpdateAsync_RuleWithNullSchedule_KeepsTheStoredWindows()
    {
        var repo = new RecordingRepository();
        var service = CreateService(new StubServiceProvider(repo, new RecordingRefresher()), SnapshotWithScheduledRule());

        var result = await service.UpdateAsync(
            enabled: true,
            adaptiveEnabled: false,
            new RateLimitTierOptions { Rpm = 60, Burst = 10, MaxConcurrentStreams = 5 },
            new Dictionary<string, RateLimitTierOptions>(),
            rules: [new RateLimitRuleDefinition("model", "gpt-4", 900, 90, 60)]);

        result.Success.Should().BeTrue(result.Message);
        var saved = repo.SavedRules!.Single();
        saved.Rpm.Should().Be(900);
        saved.Schedule.Should().ContainSingle().Which.Name.Should().Be("off-peak");
    }

    [Fact]
    public async Task UpdateAsync_RuleWithEmptySchedule_RemovesTheStoredWindows()
    {
        var repo = new RecordingRepository();
        var service = CreateService(new StubServiceProvider(repo, new RecordingRefresher()), SnapshotWithScheduledRule());

        var result = await service.UpdateAsync(
            enabled: true,
            adaptiveEnabled: false,
            new RateLimitTierOptions { Rpm = 60, Burst = 10, MaxConcurrentStreams = 5 },
            new Dictionary<string, RateLimitTierOptions>(),
            rules: [new RateLimitRuleDefinition("model", "gpt-4", 600, 60, 40) { Schedule = [] }]);

        result.Success.Should().BeTrue(result.Message);
        repo.SavedRules!.Single().Schedule.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_OverlappingWindows_IsAValidationError()
    {
        var service = CreateService(new StubServiceProvider(new RecordingRepository(), new RecordingRefresher()));
        var clash = StoredWindow with { Name = "clash", Days = ["fri"], Start = "20:00", End = "22:00" };

        var result = await service.UpdateAsync(
            enabled: true,
            adaptiveEnabled: false,
            new RateLimitTierOptions { Rpm = 60, Burst = 10, MaxConcurrentStreams = 5 },
            new Dictionary<string, RateLimitTierOptions>(),
            rules: [new RateLimitRuleDefinition("model", "gpt-4", 600, 60, 40) { Schedule = [StoredWindow, clash] }]);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(400);
        result.Message.Should().Contain("clash");
    }

    [Fact]
    public void GetSchedule_ReportsTheRuleAtTheGivenInstant()
    {
        var service = CreateService(new StubServiceProvider(null, null), SnapshotWithScheduledRule());
        var fridayEvening = new DateTimeOffset(2026, 9, 18, 19, 14, 0, TimeSpan.Zero);

        var report = service.GetSchedule(fridayEvening, fridayEvening, fridayEvening.AddDays(7), take: 10);

        var status = report.Rules.Single(r => r.Target == "gpt-4");
        status.ActiveWindow.Should().Be("off-peak");
        status.Effective.Rpm.Should().Be(1200);
        status.Base.Rpm.Should().Be(600);
        report.Occurrences.Should().NotBeEmpty();
    }

    private static RateLimitConfigAdminService CreateService(IServiceProvider provider) =>
        CreateService(provider, new GatewayConfigSnapshot());

    private static RateLimitConfigAdminService CreateService(IServiceProvider provider, GatewayConfigSnapshot snapshot) =>
        new(
            new StubConfigProvider(snapshot),
            new StubScopeFactory(provider),
            NullLogger<RateLimitConfigAdminService>.Instance);

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }

    private sealed class StubScopeFactory(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new StubScope(provider);

        private sealed class StubScope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;

            public void Dispose()
            {
            }
        }
    }

    private sealed class StubServiceProvider(
        IRateLimitSettingsRepository? repository,
        IGatewayConfigRefresher? refresher) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IRateLimitSettingsRepository))
            {
                return repository;
            }

            if (serviceType == typeof(IGatewayConfigRefresher))
            {
                return refresher;
            }

            return null;
        }
    }

    private sealed class RecordingRepository : IRateLimitSettingsRepository
    {
        public RateLimitPolicy? SavedDefault { get; private set; }

        public IReadOnlyDictionary<string, RateLimitPolicy>? SavedPlans { get; private set; }

        public IReadOnlyList<RateLimitRuleDefinition>? SavedRules { get; private set; }

        public bool? SavedEnabled { get; private set; }

        public bool? SavedAdaptiveEnabled { get; private set; }

        public long? SavedExpectedVersion { get; private set; }

        /// <summary>The version the repository claims to hold, for the conflict path.</summary>
        public long CurrentVersion { get; set; }

        public Task<long> SaveAsync(
            bool enabled,
            bool adaptiveEnabled,
            RateLimitPolicy defaultTier,
            IReadOnlyDictionary<string, RateLimitPolicy> plans,
            IReadOnlyList<RateLimitRuleDefinition> rules,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            SavedExpectedVersion = expectedVersion;

            if (expectedVersion is long expected && expected != CurrentVersion)
            {
                throw new RateLimitVersionConflictException(expected, CurrentVersion);
            }

            SavedEnabled = enabled;
            SavedAdaptiveEnabled = adaptiveEnabled;
            SavedDefault = defaultTier;
            SavedPlans = plans;
            SavedRules = rules;
            return Task.FromResult(++CurrentVersion);
        }
    }

    private sealed class RecordingRefresher : IGatewayConfigRefresher
    {
        public bool Refreshed { get; private set; }

        public Task RefreshNowAsync(CancellationToken cancellationToken = default)
        {
            Refreshed = true;
            return Task.CompletedTask;
        }
    }
}
