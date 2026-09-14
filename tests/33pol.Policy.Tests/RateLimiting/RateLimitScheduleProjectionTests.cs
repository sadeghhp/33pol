using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

/// <summary>Stored configuration with windows becomes the effective configuration the request path reads.</summary>
public sealed class RateLimitScheduleProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 19, 14, 0, TimeSpan.Zero);

    private static RateLimitWindowDefinition Active(string name = "launch", bool suspend = false) => new(
        name, RateLimitWindowKinds.Once, 3000, 500, 120, Suspend: suspend,
        From: Now.AddHours(-1), Until: Now.AddHours(1));

    private static RateLimitsConfigSection Stored(params (string Identity, RateLimitWindowDefinition Window)[] schedules) => new()
    {
        Default = new RateLimitPolicy(1000, 0, 0),
        Models = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase) { ["gpt-4"] = new(600, 60, 40) },
        TenantOverrides = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase) { ["acme"] = new(60, 10, 4) },
        Global = new RateLimitPolicy(9000, 0, 0),
        Schedules = schedules.ToDictionary(
            static s => s.Identity,
            static s => (IReadOnlyList<RateLimitWindowDefinition>)[s.Window],
            StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void Project_WithoutSchedules_ReturnsTheStoredSectionItself()
    {
        var stored = Stored();

        var (effective, next) = RateLimitScheduleProjection.Project(stored, Now, 7);

        effective.Should().BeSameAs(stored);
        next.Should().BeNull();
    }

    [Fact]
    public void Project_ActiveWindow_ReplacesTheMapEntryAndKeepsTheStoredSection()
    {
        var stored = Stored(("model:gpt-4", Active()));

        var (effective, next) = RateLimitScheduleProjection.Project(stored, Now, 7);

        effective.Models["gpt-4"].Rpm.Should().Be(3000);
        effective.Stored.Should().BeSameAs(stored);
        effective.StoredOrSelf.Models["gpt-4"].Rpm.Should().Be(600, "the admin surface reads base tiers");
        effective.EffectiveVersion.Should().Be(7);
        next.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void Project_SingletonScope_IsProjectedToo()
    {
        var stored = Stored(("global:*", Active()));

        var (effective, _) = RateLimitScheduleProjection.Project(stored, Now, 1);

        effective.Global.Rpm.Should().Be(3000);
        effective.Models["gpt-4"].Rpm.Should().Be(600);
    }

    /// <summary>
    /// A suspending window removes the rule from the projection rather than leaving an all-zero tier
    /// behind. "Enforces nothing" and "is not configured" are the same thing in every scope that
    /// looks a tier up and skips it — but not in the tenant scope, which <em>composes</em> an override
    /// with the plan tier and reads a zero rpm as "keep the plan's rate, apply only my stream cap".
    /// Absence is the only spelling of suspension that means the same thing everywhere.
    /// </summary>
    [Fact]
    public void Project_SuspendingWindow_RemovesTheRuleFromTheProjection()
    {
        var stored = Stored(("model:gpt-4", Active(suspend: true)));

        var (effective, _) = RateLimitScheduleProjection.Project(stored, Now, 1);

        effective.Models.ContainsKey("gpt-4").Should().BeFalse();
    }

    /// <summary>
    /// The case suspension actually broke. A tenant override is composed with the plan or default
    /// tier rather than replacing it, and an override with a zero rpm is the documented way to say
    /// "keep the plan\'s rate, apply only my stream cap" — so a suspended override arrived as "keep
    /// the plan\'s rate and cap streams at zero", and zero means unlimited. Pausing the rule removed
    /// the tenant\'s stream cap instead of restoring the plan\'s, letting one tenant hold open every
    /// slot in the per-model bulkhead exactly while an operator believed a restriction had been lifted.
    /// </summary>
    [Fact]
    public void Project_SuspendingWindowOnATenantRule_RestoresTheDefaultTierRatherThanUncappingStreams()
    {
        var stored = Stored(("tenant:acme", Active(suspend: true))) with
        {
            Default = new RateLimitPolicy(1000, 100, 5),
        };

        var (effective, _) = RateLimitScheduleProjection.Project(stored, Now, 1);

        effective.TenantOverrides.ContainsKey("acme").Should().BeFalse();

        var resolver = new RateLimitPolicyResolver(
            new StubConfigProvider(new GatewayConfigSnapshot { RateLimits = effective }));
        var tier = resolver.Resolve(planSlug: null, tenantId: "acme", tenantSlug: null);

        tier.MaxConcurrentStreams.Should().Be(5, "the default tier\'s cap applies while the override is paused");
        tier.Rpm.Should().Be(1000);
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }

    [Fact]
    public void Project_UpcomingWindow_KeepsTheBaseTierAndNamesTheStart()
    {
        var upcoming = Active() with { From = Now.AddHours(2), Until = Now.AddHours(3) };
        var stored = Stored(("tenant:acme", upcoming));

        var (effective, next) = RateLimitScheduleProjection.Project(stored, Now, 1);

        effective.TenantOverrides["acme"].Rpm.Should().Be(60);
        next.Should().Be(Now.AddHours(2));
    }

    [Fact]
    public void Report_ClipsAnOccurrenceThatStartedBeforeTheRange()
    {
        var rule = new RateLimitRuleDefinition("model", "gpt-4", 600, 60, 40) { Schedule = [Active()] };
        var from = Now.AddMinutes(-30);

        var report = RateLimitScheduleReportBuilder.Build([rule], Now, from, from.AddDays(7), take: 50);

        var occurrence = report.Occurrences.Should().ContainSingle().Subject;
        occurrence.Start.Should().Be(from);
        occurrence.ClippedStart.Should().BeTrue();
        occurrence.End.Should().Be(Now.AddHours(1));
        occurrence.ClippedEnd.Should().BeFalse();

        var status = report.Rules.Should().ContainSingle().Subject;
        status.ActiveWindow.Should().Be("launch");
        status.Effective.Rpm.Should().Be(3000);
        status.Base.Rpm.Should().Be(600);
        status.ActiveUntil.Should().Be(Now.AddHours(1));
        status.Windows.Single().State.Should().Be("active");

        // The window's end is the one transition inside the range: back to the base tier.
        var transition = report.Transitions.Should().ContainSingle().Subject;
        transition.At.Should().Be(Now.AddHours(1));
        transition.From.Rpm.Should().Be(3000);
        transition.To.Rpm.Should().Be(600);
        transition.Window.Should().BeNull();
    }

    /// <summary>
    /// The preview compares every window with every other one, and the endpoint feeds it a
    /// client-supplied array. The window ceiling is one of the rules validation enforces, so a set
    /// past it is refused before the scan runs rather than after — otherwise the size of a quadratic
    /// computation is the caller\'s to choose.
    /// </summary>
    [Fact]
    public void Preview_PastTheWindowCeiling_IsRefusedWithoutScanning()
    {
        var windows = Enumerable
            .Range(0, RateLimitConfigValidation.MaxWindowsPerRule + 1)
            .Select(i => Active($"w{i}"))
            .ToArray();
        var rule = new RateLimitRuleDefinition("model", "gpt-4", 600, 60, 40) { Schedule = windows };

        var preview = RateLimitWindowPreviewBuilder.Build(rule, "w0", Now);

        preview.Valid.Should().BeFalse();
        preview.Error.Should().Contain(RateLimitConfigValidation.MaxWindowsPerRule.ToString());
        preview.Overlaps.Should().BeEmpty();
    }

    /// <summary>
    /// Within the ceiling the composer still gets the whole answer, invalid rule or not — "these two
    /// clash" and "this one is outranked" are what it acts on.
    /// </summary>
    [Fact]
    public void Preview_AnInvalidRuleWithinTheCeiling_StillNamesOverlapsAndPrecedence()
    {
        var a = new RateLimitWindowDefinition(
            "nightly", RateLimitWindowKinds.Weekly, 120, 0, 4,
            Days: ["mon"], Start: "22:00", End: "23:00", TimeZone: "UTC");
        var b = new RateLimitWindowDefinition(
            "also-nightly", RateLimitWindowKinds.Weekly, 240, 0, 4,
            Days: ["mon"], Start: "22:30", End: "23:30", TimeZone: "UTC");
        var rule = new RateLimitRuleDefinition("model", "gpt-4", 600, 60, 40) { Schedule = [a, b] };

        var preview = RateLimitWindowPreviewBuilder.Build(rule, "also-nightly", Now);

        preview.Valid.Should().BeFalse();
        preview.Overlaps.Should().ContainSingle().Which.Should().Be("nightly");
    }

    /// <summary>
    /// Occurrences are capped the way transitions already were. They are built in the same loop and
    /// grow faster — one per matching day, per window, per rule — so the list was the only unbounded
    /// thing in a response an operator can repeat.
    /// </summary>
    [Fact]
    public void Report_TruncatesOccurrencesAndSaysSo()
    {
        var daily = new RateLimitWindowDefinition(
            "nightly", RateLimitWindowKinds.Weekly, 120, 0, 4,
            Days: ["mon", "tue", "wed", "thu", "fri", "sat", "sun"], Start: "22:00", End: "23:00", TimeZone: "UTC");

        // 400 rules x 7 nights x 8 weeks is well past the ceiling and nowhere near the configured one.
        var rules = Enumerable
            .Range(0, 400)
            .Select(i => new RateLimitRuleDefinition("model", $"m{i}", 600, 0, 0) { Schedule = [daily] })
            .ToArray();

        var report = RateLimitScheduleReportBuilder.Build(rules, Now, Now, Now.AddDays(56), take: 50);

        report.Occurrences.Should().HaveCount(RateLimitScheduleReportBuilder.MaxOccurrences);
        report.OccurrencesTruncated.Should().BeTrue();
        report.OccurrencesTotal.Should().BeGreaterThan(RateLimitScheduleReportBuilder.MaxOccurrences);
        report.Occurrences.Should().BeInAscendingOrder(o => o.Start, "the soonest are what a calendar draws first");
    }

    [Fact]
    public void Report_TruncatesTransitionsAndSaysSo()
    {
        var weekly = new RateLimitWindowDefinition(
            "nightly", RateLimitWindowKinds.Weekly, 120, 0, 4,
            Days: ["mon", "tue", "wed", "thu", "fri", "sat", "sun"], Start: "22:00", End: "04:00", TimeZone: "UTC");
        var rule = new RateLimitRuleDefinition("api_key", "6f1c", 30, 0, 2) { Schedule = [weekly] };

        var report = RateLimitScheduleReportBuilder.Build([rule], Now, Now, Now.AddDays(7), take: 3);

        report.Transitions.Should().HaveCount(3);
        report.TransitionsTruncated.Should().BeTrue();
        report.TransitionsTotal.Should().Be(14, "seven nights, a start and an end each");
        report.Transitions.Should().BeInAscendingOrder(t => t.At);
    }

    [Fact]
    public void Report_InvalidWindow_IsReportedAndTheBaseTierApplies()
    {
        var broken = new RateLimitWindowDefinition(
            "off-peak", RateLimitWindowKinds.Weekly, 1200, 200, 80,
            Days: ["mon"], Start: "19:00", End: "07:00", TimeZone: "Europe/Berlinn");
        var rule = new RateLimitRuleDefinition("model", "gpt-4", 600, 60, 40) { Schedule = [broken] };

        var report = RateLimitScheduleReportBuilder.Build([rule], Now, Now, Now.AddDays(7), take: 50);

        var status = report.Rules.Single();
        status.Effective.Rpm.Should().Be(600);
        status.Windows.Single().State.Should().Be("invalid");
        status.Windows.Single().Error.Should().Contain("Europe/Berlinn");
        report.Occurrences.Should().BeEmpty();
    }

    [Fact]
    public void Preview_NamesOverlapsAndPrecedence()
    {
        var offPeak = new RateLimitWindowDefinition(
            "off-peak", RateLimitWindowKinds.Weekly, 1200, 200, 80,
            Days: ["mon", "tue", "wed", "thu", "fri"], Start: "19:00", End: "07:00", TimeZone: "Europe/Berlin");
        var weekend = new RateLimitWindowDefinition(
            "weekend", RateLimitWindowKinds.Weekly, 1500, 300, 100,
            Days: ["sat", "sun"], Start: "00:00", End: "24:00", TimeZone: "Europe/Berlin");
        var launch = Active();
        var rule = new RateLimitRuleDefinition("model", "gpt-4", 600, 60, 40) { Schedule = [offPeak, launch, weekend] };

        var preview = RateLimitWindowPreviewBuilder.Build(rule, "weekend", Now);

        preview.Valid.Should().BeFalse();
        preview.Overlaps.Should().ContainSingle().Which.Should().Be("off-peak");
        preview.OutrankedBy.Should().ContainSingle().Which.Should().Be("launch");
        preview.Outranks.Should().BeEmpty();
        preview.NextStartAt.Should().Be(new DateTimeOffset(2026, 9, 18, 22, 0, 0, TimeSpan.Zero), "Saturday 00:00 CEST");
    }
}
