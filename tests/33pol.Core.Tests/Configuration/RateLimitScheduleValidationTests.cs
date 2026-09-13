using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Core.Tests.Configuration;

public sealed class RateLimitScheduleValidationTests
{
    private const string Berlin = "Europe/Berlin";

    private static RateLimitRuleDefinition Rule(params RateLimitWindowDefinition[] windows) =>
        new("model", "gpt-4", 600, 60, 40) { Schedule = windows };

    private static RateLimitWindowDefinition Weekly(string name, string[] days, string start, string end, string zone = Berlin, int? priority = null) =>
        new(name, RateLimitWindowKinds.Weekly, 1200, 200, 80, Priority: priority, Days: days, Start: start, End: end, TimeZone: zone);

    private static RateLimitWindowDefinition Once(string name, DateTimeOffset from, DateTimeOffset? until, DateTimeOffset? validUntil = null) =>
        new(name, RateLimitWindowKinds.Once, 3000, 500, 120, From: from, Until: until, ValidUntil: validUntil);

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
