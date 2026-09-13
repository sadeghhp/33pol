using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

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

    [Fact]
    public void Project_SuspendingWindow_LeavesAnUnlimitedEntry()
    {
        var stored = Stored(("model:gpt-4", Active(suspend: true)));

        var (effective, _) = RateLimitScheduleProjection.Project(stored, Now, 1);

        effective.Models["gpt-4"].EnforcesNothing.Should().BeTrue();
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
