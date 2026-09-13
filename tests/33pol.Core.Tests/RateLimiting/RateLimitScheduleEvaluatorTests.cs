using Pol33.Core.RateLimiting;

namespace Pol33.Core.Tests.RateLimiting;

/// <summary>
/// The time arithmetic behind scheduled windows, pinned to real instants in Europe/Berlin so the
/// daylight-saving cases are the zone's actual 2026 transitions rather than a made-up offset.
/// </summary>
public sealed class RateLimitScheduleEvaluatorTests
{
    private const string Berlin = "Europe/Berlin";

    // Friday 18 September 2026, 21:14 in Berlin (CEST, UTC+2).
    private static readonly DateTimeOffset FridayEvening = new(2026, 9, 18, 19, 14, 0, TimeSpan.Zero);

    private static readonly RateLimitPolicy Base = new(600, 60, 40);

    private static RateLimitWindowDefinition OffPeak(int? priority = null) => new(
        "off-peak",
        RateLimitWindowKinds.Weekly,
        1200,
        200,
        80,
        Priority: priority,
        Days: ["mon", "tue", "wed", "thu", "fri"],
        Start: "19:00",
        End: "07:00",
        TimeZone: Berlin);

    [Fact]
    public void Weekly_OvernightWindow_IsActiveAfterItsStart()
    {
        RateLimitScheduleEvaluator.TryGetOccurrence(OffPeak(), FridayEvening, out var occurrence).Should().BeTrue();

        // 19:00 CEST is 17:00Z; 07:00 CEST the next morning is 05:00Z.
        occurrence.Start.Should().Be(new DateTimeOffset(2026, 9, 18, 17, 0, 0, TimeSpan.Zero));
        occurrence.End.Should().Be(new DateTimeOffset(2026, 9, 19, 5, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Weekly_OvernightWindow_IsStillActiveAfterMidnightOnADayItDoesNotStartOn()
    {
        // Saturday 00:30 CEST: Saturday is not a start day, but Friday's occurrence is still running.
        var saturdayNight = new DateTimeOffset(2026, 9, 18, 22, 30, 0, TimeSpan.Zero);

        RateLimitScheduleEvaluator.TryGetOccurrence(OffPeak(), saturdayNight, out var occurrence).Should().BeTrue();
        occurrence.Start.Should().Be(new DateTimeOffset(2026, 9, 18, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Weekly_WindowDoesNotStartOnAnUnlistedDay()
    {
        // Saturday 21:00 CEST: no occurrence starts on Saturday, and Friday's ended at 07:00.
        var saturdayEvening = new DateTimeOffset(2026, 9, 19, 19, 0, 0, TimeSpan.Zero);

        RateLimitScheduleEvaluator.TryGetOccurrence(OffPeak(), saturdayEvening, out _).Should().BeFalse();
    }

    [Fact]
    public void Weekly_NextOccurrence_SkipsToTheNextListedDay()
    {
        var saturdayNoon = new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

        var next = RateLimitScheduleEvaluator.NextOccurrence(OffPeak(), saturdayNoon);

        next.Should().NotBeNull();
        next!.Value.Start.Should().Be(new DateTimeOffset(2026, 9, 21, 17, 0, 0, TimeSpan.Zero), "Monday 19:00 CEST");
    }

    [Fact]
    public void Weekly_EndAtTwentyFour_RunsToTheEndOfTheDay()
    {
        var weekend = new RateLimitWindowDefinition(
            "weekend", RateLimitWindowKinds.Weekly, 1500, 300, 100,
            Days: ["sat", "sun"], Start: "07:00", End: "24:00", TimeZone: Berlin);

        var saturdayNoon = new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
        RateLimitScheduleEvaluator.TryGetOccurrence(weekend, saturdayNoon, out var occurrence).Should().BeTrue();

        occurrence.Start.Should().Be(new DateTimeOffset(2026, 9, 19, 5, 0, 0, TimeSpan.Zero));
        occurrence.End.Should().Be(new DateTimeOffset(2026, 9, 19, 22, 0, 0, TimeSpan.Zero), "midnight CEST is 22:00Z");
    }

    [Fact]
    public void Weekly_StartInsideTheSpringGap_MovesToTheFirstValidInstant()
    {
        // 29 March 2026: Berlin skips 02:00–03:00. A 02:30 start does not exist and becomes 03:30 CEST = 01:30Z.
        var window = new RateLimitWindowDefinition(
            "gap", RateLimitWindowKinds.Weekly, 100, 0, 0,
            Days: ["sun"], Start: "02:30", End: "05:00", TimeZone: Berlin);

        var justAfter = new DateTimeOffset(2026, 3, 29, 1, 45, 0, TimeSpan.Zero);
        RateLimitScheduleEvaluator.TryGetOccurrence(window, justAfter, out var occurrence).Should().BeTrue();

        occurrence.Start.Should().Be(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero));
        occurrence.End.Should().Be(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.Zero), "05:00 CEST");
    }

    [Fact]
    public void Weekly_WindowAcrossTheAutumnRepeat_LastsAnHourLonger()
    {
        // 25 October 2026: Berlin repeats 02:00–03:00. Saturday 22:00 CEST → Sunday 07:00 CET is ten hours.
        var window = new RateLimitWindowDefinition(
            "night", RateLimitWindowKinds.Weekly, 100, 0, 0,
            Days: ["sat"], Start: "22:00", End: "07:00", TimeZone: Berlin);

        var saturdayNight = new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero);
        RateLimitScheduleEvaluator.TryGetOccurrence(window, saturdayNight, out var occurrence).Should().BeTrue();

        occurrence.Start.Should().Be(new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero));
        occurrence.End.Should().Be(new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.Zero), "07:00 CET is 06:00Z");
        (occurrence.End - occurrence.Start).Should().Be(TimeSpan.FromHours(10));
    }

    [Fact]
    public void Weekly_ValidUntil_BoundsTheOccurrences()
    {
        var window = OffPeak() with { ValidUntil = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero) };

        RateLimitScheduleEvaluator.TryGetOccurrence(window, FridayEvening, out _).Should().BeTrue();
        RateLimitScheduleEvaluator.NextOccurrence(window, FridayEvening).Should().BeNull("Monday's occurrence starts after the bound");
    }

    [Fact]
    public void Once_OpenEnded_IsActiveForeverAfterFrom()
    {
        var window = new RateLimitWindowDefinition(
            "new-baseline", RateLimitWindowKinds.Once, 900, 90, 60,
            From: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

        var before = RateLimitScheduleEvaluator.Evaluate(Base, [window], FridayEvening);
        before.IsBase.Should().BeTrue();
        before.NextTransition.Should().Be(window.From);

        var after = RateLimitScheduleEvaluator.Evaluate(Base, [window], new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero));
        after.ActiveWindow.Should().Be(window);
        after.Effective.Rpm.Should().Be(900);
        after.ActiveUntil.Should().BeNull();
        after.NextTransition.Should().BeNull("nothing is scheduled after an open-ended window");
    }

    [Fact]
    public void Evaluate_OnceOutranksWeekly_WhenBothAreActive()
    {
        var launch = new RateLimitWindowDefinition(
            "launch", RateLimitWindowKinds.Once, 3000, 500, 120,
            From: new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero),
            Until: new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));

        var evaluation = RateLimitScheduleEvaluator.Evaluate(Base, [OffPeak(), launch], FridayEvening);

        evaluation.ActiveWindow!.Name.Should().Be("launch");
        evaluation.Effective.Rpm.Should().Be(3000);
        evaluation.ActiveUntil.Should().Be(launch.Until);
        // The weekly window ends Saturday 05:00Z, before the launch does: that is the earliest moment
        // anything could change, even if nothing visible does.
        evaluation.NextTransition.Should().Be(new DateTimeOffset(2026, 9, 19, 5, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Evaluate_ExplicitPriority_BeatsTheKindDefault()
    {
        var launch = new RateLimitWindowDefinition(
            "launch", RateLimitWindowKinds.Once, 3000, 500, 120,
            From: new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero),
            Until: new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));

        var evaluation = RateLimitScheduleEvaluator.Evaluate(Base, [OffPeak(priority: 500), launch], FridayEvening);

        evaluation.ActiveWindow!.Name.Should().Be("off-peak");
    }

    [Fact]
    public void Evaluate_SuspendingWindow_EnforcesNothing()
    {
        var pause = new RateLimitWindowDefinition(
            "import", RateLimitWindowKinds.Once, 0, 0, 0, Suspend: true,
            From: FridayEvening.AddHours(-1), Until: FridayEvening.AddHours(1));

        var evaluation = RateLimitScheduleEvaluator.Evaluate(Base, [pause], FridayEvening);

        evaluation.Suspended.Should().BeTrue();
        evaluation.Effective.EnforcesNothing.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_UnresolvableZone_FallsBackToTheBaseTier()
    {
        var broken = OffPeak() with { TimeZone = "Europe/Berlinn" };

        RateLimitScheduleEvaluator.Describe(broken).Should().Contain("not known");

        var evaluation = RateLimitScheduleEvaluator.Evaluate(Base, [broken], FridayEvening);
        evaluation.IsBase.Should().BeTrue();
        evaluation.Effective.Should().Be(Base);
    }

    [Fact]
    public void Evaluate_NoWindows_IsTheBaseTierWithNoTransition()
    {
        var evaluation = RateLimitScheduleEvaluator.Evaluate(Base, [], FridayEvening);

        evaluation.Effective.Should().Be(Base);
        evaluation.NextTransition.Should().BeNull();
    }

    [Fact]
    public void OccurrencesBetween_IncludesAnOccurrenceThatStartedBeforeTheRange()
    {
        // Range starts Friday 06:00Z, inside Thursday night's occurrence (Thu 17:00Z → Fri 05:00Z is
        // already over; Thursday 19:00 CEST → Friday 07:00 CEST = 17:00Z → 05:00Z). Use 04:00Z instead.
        var from = new DateTimeOffset(2026, 9, 18, 4, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(7);

        var occurrences = RateLimitScheduleEvaluator.OccurrencesBetween(OffPeak(), from, to).ToList();

        occurrences.Should().NotBeEmpty();
        occurrences[0].Start.Should().Be(new DateTimeOffset(2026, 9, 17, 17, 0, 0, TimeSpan.Zero), "Thursday's occurrence is still running at the range start");
        occurrences[0].End.Should().Be(new DateTimeOffset(2026, 9, 18, 5, 0, 0, TimeSpan.Zero));
        // Thu(clipped), Fri, Mon, Tue, Wed, Thu: six occurrences intersect a seven-day range.
        occurrences.Should().HaveCount(6);
    }

    [Theory]
    [InlineData("mon", DayOfWeek.Monday)]
    [InlineData("SUN", DayOfWeek.Sunday)]
    [InlineData(" fri ", DayOfWeek.Friday)]
    public void TryParseDay_AcceptsTheThreeLetterLabels(string label, DayOfWeek expected)
    {
        RateLimitScheduleEvaluator.TryParseDay(label, out var day).Should().BeTrue();
        day.Should().Be(expected);
    }

    [Theory]
    [InlineData("monday")]
    [InlineData("")]
    [InlineData("8")]
    public void TryParseDay_RejectsAnythingElse(string label)
    {
        RateLimitScheduleEvaluator.TryParseDay(label, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("24:00", 24)]
    [InlineData("00:00", 0)]
    [InlineData("19:30", 19.5)]
    public void TryParseTime_ReadsWallClockTimes(string text, double hours)
    {
        RateLimitScheduleEvaluator.TryParseTime(text, out var time).Should().BeTrue();
        time.Should().Be(TimeSpan.FromHours(hours));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("7pm")]
    [InlineData("")]
    public void TryParseTime_RejectsNonTimes(string text)
    {
        RateLimitScheduleEvaluator.TryParseTime(text, out _).Should().BeFalse();
    }
}
