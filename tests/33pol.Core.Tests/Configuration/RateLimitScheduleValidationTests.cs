using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Core.Tests.Configuration;

public sealed class RateLimitScheduleValidationTests
{
    /// <summary>
    /// Two windows of different kinds were never compared, which is safe only while their default
    /// ranks differ — <c>once</c> outranks <c>weekly</c>. Priority is operator-settable, so an equal
    /// explicit rank across kinds produced two windows that could be active together with the winner
    /// decided by start time and then by name: the arbitrary answer the equal-rank refusal exists to
    /// prevent.
    /// </summary>
    [Fact]
    public void TryValidateSchedule_AOnceAndAWeeklyWindowAtTheSamePriority_AreRejected()
    {
        var once = new RateLimitWindowDefinition(
            "launch", RateLimitWindowKinds.Once, 3000, 0, 0, Priority: 150,
            From: new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            Until: new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var weekly = new RateLimitWindowDefinition(
            "nightly", RateLimitWindowKinds.Weekly, 120, 0, 0, Priority: 150,
            Days: ["mon"], Start: "22:00", End: "23:00", TimeZone: "UTC");
        var rule = new RateLimitRuleDefinition("model", "gpt-4", 600, 0, 0) { Schedule = [once, weekly] };

        RateLimitConfigValidation.TryValidateSchedule(rule, out var error).Should().BeFalse();
        error.Should().Contain("same time");
    }

    /// <summary>Different ranks settle the pair, whatever their kinds — that is what a priority is for.</summary>
    [Fact]
    public void TryValidateSchedule_AOnceAndAWeeklyWindowAtDifferentPriorities_AreAccepted()
    {
        var once = new RateLimitWindowDefinition(
            "launch", RateLimitWindowKinds.Once, 3000, 0, 0, Priority: 200,
            From: new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            Until: new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var weekly = new RateLimitWindowDefinition(
            "nightly", RateLimitWindowKinds.Weekly, 120, 0, 0, Priority: 100,
            Days: ["mon"], Start: "22:00", End: "23:00", TimeZone: "UTC");
        var rule = new RateLimitRuleDefinition("model", "gpt-4", 600, 0, 0) { Schedule = [once, weekly] };

        RateLimitConfigValidation.TryValidateSchedule(rule, out var error).Should().BeTrue(error);
    }

    private const string Berlin = "Europe/Berlin";

    private static RateLimitRuleDefinition Rule(params RateLimitWindowDefinition[] windows) =>
        new("model", "gpt-4", 600, 60, 40) { Schedule = windows };

    private static RateLimitWindowDefinition Weekly(string name, string[] days, string start, string end, string zone = Berlin, int? priority = null) =>
        new(name, RateLimitWindowKinds.Weekly, 1200, 200, 80, Priority: priority, Days: days, Start: start, End: end, TimeZone: zone);

    private static RateLimitWindowDefinition Once(string name, DateTimeOffset from, DateTimeOffset? until, DateTimeOffset? validUntil = null) =>
        new(name, RateLimitWindowKinds.Once, 3000, 500, 120, From: from, Until: until, ValidUntil: validUntil);

    [Fact]
    public void Validate_WeeklyWindowWithMoreThanSevenDays_IsRefused()
    {
        var rule = Rule(Weekly("all", ["mon", "tue", "wed", "thu", "fri", "sat", "sun", "mon"], "01:00", "02:00"));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("at most 7 days");
    }

    [Theory]
    [InlineData("mon", "mon")]
    [InlineData("mon", "MON")]
    [InlineData("sat", " sat ")]
    public void Validate_WeeklyWindowWithADuplicateDay_IsRefused(string first, string second)
    {
        var rule = Rule(Weekly("twice", [first, "wed", second], "01:00", "02:00"));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("more than once");
    }

    [Fact]
    public void Validate_WeeklyWindowWithAnUnknownDay_StillNamesTheDay()
    {
        var rule = Rule(Weekly("odd", ["mon", "funday"], "01:00", "02:00"));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("'funday' is not a day");
    }

    [Theory]
    [InlineData("mon")]
    [InlineData("sat,sun")]
    [InlineData("mon,wed,fri")]
    [InlineData("mon,tue,wed,thu,fri,sat,sun")]
    [InlineData("sun,sat,fri,thu,wed,tue,mon")]
    public void Validate_WeeklyWindowWithUniqueDays_IsAccepted(string days)
    {
        var rule = Rule(Weekly("ok", days.Split(','), "01:00", "02:00"));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeTrue(error);
    }

    /// <summary>
    /// The days list is client-supplied and was unbounded, and the overlap check compares every span
    /// of one window with every span of another while re-parsing the inner window per outer span:
    /// two windows of 20,000 repeated days cost 95 seconds of uncancellable CPU and were then
    /// accepted. The refusal is now decided from the count alone, before any pair is compared, on
    /// the save path and on the overlap scan the previews run for a rule that is already invalid.
    /// </summary>
    [Fact]
    public void Validate_HugeRepeatedDayLists_AreRefusedWithoutComparingSpans()
    {
        var mondays = Enumerable.Repeat("mon", 200_000).ToArray();
        var wednesdays = Enumerable.Repeat("wed", 200_000).ToArray();
        var windows = Enumerable.Range(0, RateLimitConfigValidation.MaxWindowsPerRule)
            .Select(i => Weekly("w" + i, i % 2 == 0 ? mondays : wednesdays, "01:00", "02:00"))
            .ToArray();
        var rule = Rule(windows);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var valid = RateLimitConfigValidation.TryValidateRules([rule], out var error);
        var overlaps = RateLimitConfigValidation.FindWindowOverlaps(windows);
        watch.Stop();

        valid.Should().BeFalse();
        error.Should().Contain("at most 7 days");
        overlaps.Should().BeEmpty("a window that is not well formed is never compared");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the old path needed minutes for a tenth of this input");
    }

    /// <summary>Seven days against seven days, wrapping midnight and the week: the materialised inner list must still see every span.</summary>
    [Fact]
    public void Validate_FullWeekWindowsThatWrapTheWeek_StillOverlap()
    {
        string[] week = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
        var windows = new[] { Weekly("late", week, "23:00", "01:00"), Weekly("early", ["mon"], "00:30", "00:45") };

        RateLimitConfigValidation.FindWindowOverlaps(windows).Should().ContainSingle()
            .Which.Should().Be(("late", "early"));
    }

    [Fact]
    public void Validate_OverlappingWeeklyWindows_WithTheSamePriority_AreRefused()
    {
        var rule = Rule(
            Weekly("off-peak", ["mon", "tue", "wed", "thu", "fri"], "19:00", "07:00", priority: 150),
            Weekly("weekend", ["sat", "sun"], "00:00", "24:00", priority: 150));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("off-peak").And.Contain("weekend").And.Contain("priority");
    }

    [Fact]
    public void Validate_OverlappingWeeklyWindows_WithAnExplicitPriorityEqualToTheDefault_AreRefused()
    {
        // The weekly default rank is 100; an explicit 100 ties with it exactly as no priority would.
        var rule = Rule(
            Weekly("off-peak", ["mon", "tue", "wed", "thu", "fri"], "19:00", "07:00"),
            Weekly("weekend", ["sat", "sun"], "00:00", "24:00", priority: 100));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
    }

    [Fact]
    public void Validate_OnceWindows_WhoseValidityNeverMeets_AreAllowed()
    {
        var rule = Rule(
            Once("launch", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), validUntil: new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero)),
            Once("new-baseline", new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), null));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeTrue(error);
    }

    [Fact]
    public void Validate_NullSchedule_IsUnspecifiedAndValid()
    {
        var rule = new RateLimitRuleDefinition("model", "gpt-4", 600, 60, 40);

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeTrue(error);
    }

    [Fact]
    public void Validate_WeeklyAndOnce_Coexist()
    {
        var rule = Rule(
            Weekly("off-peak", ["mon", "tue", "wed", "thu", "fri"], "19:00", "07:00"),
            Once("launch", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero)));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeTrue(error);
    }

    [Fact]
    public void Validate_OverlappingWeeklyWindows_AreRefused()
    {
        // off-peak runs Friday 19:00 → Saturday 07:00; an all-day weekend window overlaps Saturday 00:00–07:00.
        var rule = Rule(
            Weekly("off-peak", ["mon", "tue", "wed", "thu", "fri"], "19:00", "07:00"),
            Weekly("weekend", ["sat", "sun"], "00:00", "24:00"));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("off-peak").And.Contain("weekend").And.Contain("same kind");
    }

    [Fact]
    public void Validate_AdjacentWeeklyWindows_DoNotOverlap()
    {
        var rule = Rule(
            Weekly("off-peak", ["mon", "tue", "wed", "thu", "fri"], "19:00", "07:00"),
            Weekly("weekend", ["sat", "sun"], "07:00", "24:00"));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeTrue(error);
    }

    [Fact]
    public void Validate_OverlappingWeeklyWindows_WithAPriority_AreAllowed()
    {
        var rule = Rule(
            Weekly("off-peak", ["mon", "tue", "wed", "thu", "fri"], "19:00", "07:00"),
            Weekly("weekend", ["sat", "sun"], "00:00", "24:00", priority: 150));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeTrue(error);
    }

    [Fact]
    public void Validate_OverlappingOnceWindows_AreRefused()
    {
        var rule = Rule(
            Once("launch", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero)),
            Once("new-baseline", new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), null));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("launch").And.Contain("new-baseline");
    }

    [Fact]
    public void Validate_UntilBeforeFrom_IsRefused()
    {
        var rule = Rule(Once("launch", new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("until must be after from");
    }

    [Fact]
    public void Validate_TwoZonesOnOneRule_AreRefused()
    {
        var rule = Rule(
            Weekly("europe", ["mon"], "09:00", "17:00"),
            Weekly("america", ["tue"], "09:00", "17:00", zone: "America/New_York"));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("same time zone");
    }

    [Fact]
    public void Validate_UnknownZone_IsRefused()
    {
        var rule = Rule(Weekly("off-peak", ["mon"], "19:00", "07:00", zone: "Europe/Berlinn"));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("Europe/Berlinn");
    }

    [Fact]
    public void Validate_WindowThatEnforcesNothing_NeedsSuspend()
    {
        var silent = new RateLimitWindowDefinition("pause", RateLimitWindowKinds.Once, 0, 0, 0,
            From: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        RateLimitConfigValidation.TryValidateRules([Rule(silent)], out var error).Should().BeFalse();
        error.Should().Contain("enforces nothing");

        RateLimitConfigValidation.TryValidateRules([Rule(silent with { Suspend = true })], out error).Should().BeTrue(error);
    }

    [Fact]
    public void Validate_DuplicateWindowNames_AreRefused()
    {
        var rule = Rule(
            Weekly("night", ["mon"], "19:00", "07:00"),
            Weekly("Night", ["tue"], "19:00", "07:00"));

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("more than once");
    }

    [Fact]
    public void Validate_TenantWindowWithRpmZero_MustHaveNoBurst()
    {
        var window = new RateLimitWindowDefinition("cap", RateLimitWindowKinds.Once, 0, 10, 4,
            From: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var rule = new RateLimitRuleDefinition("tenant", "acme", 0, 0, 8) { Schedule = [window] };

        RateLimitConfigValidation.TryValidateRules([rule], out var error).Should().BeFalse();
        error.Should().Contain("burst");
    }

    [Fact]
    public void Validate_TooManyWindows_IsRefused()
    {
        var windows = Enumerable.Range(0, RateLimitConfigValidation.MaxWindowsPerRule + 1)
            .Select(i => Once("w" + i, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i * 2), new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i * 2 + 1)))
            .ToArray();

        RateLimitConfigValidation.TryValidateRules([Rule(windows)], out var error).Should().BeFalse();
        error.Should().Contain("more than");
    }

    [Fact]
    public void FindWindowOverlaps_NamesThePairs()
    {
        var overlaps = RateLimitConfigValidation.FindWindowOverlaps(
        [
            Weekly("a", ["mon"], "08:00", "12:00"),
            Weekly("b", ["mon"], "11:00", "13:00"),
            Weekly("c", ["tue"], "11:00", "13:00"),
        ]);

        overlaps.Should().ContainSingle().Which.Should().Be(("a", "b"));
    }
}
