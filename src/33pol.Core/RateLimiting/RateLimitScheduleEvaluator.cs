using System.Globalization;

namespace Pol33.Core.RateLimiting;

/// <summary>One span during which a window is active: <c>[Start, End)</c> in UTC.</summary>
public readonly record struct WindowOccurrence(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Contains(DateTimeOffset instant) => Start <= instant && instant < End;
}

/// <summary>What a rule enforces at one instant, and when that next changes.</summary>
/// <param name="Effective">The tier in force: the winning window's, or the base tier.</param>
/// <param name="ActiveWindow">The window whose tier is in force, or null for the base tier.</param>
/// <param name="ActiveUntil">When the active window's current occurrence ends; null for the base tier or an open-ended window.</param>
/// <param name="NextTransition">
/// The earliest instant at which the answer might differ, or null when nothing is scheduled ahead.
/// Conservative: it may name an instant at which nothing visible changes, never one later than a
/// real change.
/// </param>
/// <param name="Suspended">Whether the active window suspends the rule outright.</param>
public sealed record ScheduleEvaluation(
    RateLimitPolicy Effective,
    RateLimitWindowDefinition? ActiveWindow,
    DateTimeOffset? ActiveUntil,
    DateTimeOffset? NextTransition,
    bool Suspended)
{
    public bool IsBase => ActiveWindow is null;
}

/// <summary>
/// The pure time arithmetic behind scheduled windows: which window is active at an instant, when
/// it ends, when the next one starts. Every answer is a function of the stored definitions and the
/// clock it is handed, so a restart, a missed tick or a crashed timer can recover by asking again.
/// </summary>
/// <remarks>
/// <para>Weekly windows are read in their own zone with the zone's daylight-saving rules: a window
/// that starts at a non-existent local time starts at the next valid instant, and one that spans
/// the repeated hour lasts an hour longer. A window whose zone the host cannot resolve, or whose
/// times do not parse, is never active — the base tier applies — rather than being guessed at.</para>
///
/// <para>Precedence among windows active at the same instant is by <see cref="RateLimitWindowDefinition.Rank"/>:
/// a <c>once</c> window beats a <c>weekly</c> one unless an explicit priority says otherwise, and
/// same-kind overlaps are refused by validation so they do not arise from a saved configuration.</para>
/// </remarks>
public static class RateLimitScheduleEvaluator
{
    private static readonly string[] DayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static readonly TimeSpan EndOfDay = TimeSpan.FromHours(24);

    /// <summary>The day labels a weekly window accepts, Monday first.</summary>
    public static IReadOnlyList<string> Days => DayNames;

    public static bool TryParseDay(string? label, out DayOfWeek day)
    {
        day = DayOfWeek.Monday;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var index = Array.FindIndex(DayNames, d => string.Equals(d, label.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        // DayNames is Monday-first; DayOfWeek is Sunday-first.
        day = (DayOfWeek)((index + 1) % 7);
        return true;
    }

    /// <summary>Parses <c>HH:mm</c> (or <c>HH:mm:ss</c>); <c>24:00</c> is accepted as the end of the day.</summary>
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed is "24:00" or "24:00:00")
        {
            time = EndOfDay;
            return true;
        }

        if (!TimeSpan.TryParseExact(trimmed, [@"hh\:mm", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out time))
        {
            return false;
        }

        return time >= TimeSpan.Zero && time < EndOfDay;
    }

    /// <summary>Resolves an IANA (or Windows) zone id; null or empty means UTC.</summary>
    public static bool TryResolveTimeZone(string? id, out TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id.Trim(), "UTC", StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        zone = TimeZoneInfo.Utc;
        return false;
    }

    /// <summary>
    /// Whether the window can be evaluated at all. A window that is not is never active and is
    /// reported as invalid, so the base tier applies and the operator can see why.
    /// </summary>
    public static bool IsWellFormed(RateLimitWindowDefinition window) => Describe(window) is null;

    /// <summary>Why a window cannot be evaluated, or null when it can.</summary>
    public static string? Describe(RateLimitWindowDefinition window)
    {
        if (window.IsOnce)
        {
            if (window.From is null)
            {
                return "a once window needs a from instant";
            }

            if (window.Until is not null && window.Until <= window.From)
            {
                return "until must be after from";
            }

            return null;
        }

        if (!window.IsWeekly)
        {
            return $"unknown window kind '{window.Kind}'";
        }

        if (window.Days is null || window.Days.Count == 0)
        {
            return "a weekly window needs at least one day";
        }

        // Bounded before anything walks the list. The days are client-supplied, the overlap check
        // compares every span of one window with every span of another, and a list that repeats a
        // day says nothing a shorter one does not — so a week's worth is the ceiling, and past it
        // the answer is known without reading a single entry.
        if (window.Days.Count > DayNames.Length)
        {
            return $"a weekly window lists each day at most once, so at most {DayNames.Length} days";
        }

        var seenDays = 0;
        foreach (var label in window.Days)
        {
            if (!TryParseDay(label, out var day))
            {
                return $"'{label}' is not a day; use {string.Join(", ", DayNames)}";
            }

            var bit = 1 << (int)day;
            if ((seenDays & bit) != 0)
            {
                return $"'{label.Trim()}' is listed more than once; a weekly window lists each day at most once";
            }

            seenDays |= bit;
        }

        if (!TryParseTime(window.Start, out var start) || start == EndOfDay)
        {
            return "start must be a time of day, HH:mm";
        }

        if (!TryParseTime(window.End, out var end))
        {
            return "end must be a time of day, HH:mm, or 24:00";
        }

        if (start == end)
        {
            return "start and end must differ; use 00:00 to 24:00 for a whole day";
        }

        if (!TryResolveTimeZone(window.TimeZone, out _))
        {
            return $"time zone '{window.TimeZone}' is not known on this host";
        }

        if (window.ValidFrom is not null && window.ValidUntil is not null && window.ValidUntil <= window.ValidFrom)
        {
            return "valid until must be after valid from";
        }

        return null;
    }

    /// <summary>The occurrence of this window that contains <paramref name="instant"/>, if any.</summary>
    public static bool TryGetOccurrence(
        RateLimitWindowDefinition window,
        DateTimeOffset instant,
        out WindowOccurrence occurrence)
    {
        occurrence = default;
        if (!IsWellFormed(window))
        {
            return false;
        }

        if (window.IsOnce)
        {
            var once = OnceOccurrence(window);
            if (once is { } o && o.Contains(instant))
            {
                occurrence = o;
                return true;
            }

            return false;
        }

        TryResolveTimeZone(window.TimeZone, out var zone);
        var local = TimeZoneInfo.ConvertTime(instant, zone).Date;

        // A window is at most 24 hours long, so the occurrence containing this instant started
        // today or yesterday in local terms.
        for (var back = 0; back <= 1; back++)
        {
            if (TryBuildWeeklyOccurrence(window, zone, local.AddDays(-back), out var candidate) &&
                candidate.Contains(instant))
            {
                occurrence = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The first occurrence that starts strictly after <paramref name="instant"/>, or null.</summary>
    public static WindowOccurrence? NextOccurrence(RateLimitWindowDefinition window, DateTimeOffset instant)
    {
        if (!IsWellFormed(window))
        {
            return null;
        }

        if (window.IsOnce)
        {
            var once = OnceOccurrence(window);
            return once is { } o && o.Start > instant ? o : null;
        }

        TryResolveTimeZone(window.TimeZone, out var zone);

        // Search from the later of now and the window's own start bound, so a window that only
        // becomes valid months ahead is found without walking every day in between.
        var searchFrom = window.ValidFrom is { } validFrom && validFrom > instant ? validFrom : instant;
        var local = TimeZoneInfo.ConvertTime(searchFrom, zone).Date;

        for (var ahead = 0; ahead <= 7; ahead++)
        {
            if (TryBuildWeeklyOccurrence(window, zone, local.AddDays(ahead), out var candidate) &&
                candidate.Start > instant)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Every occurrence that intersects <c>[from, to)</c>, unclipped and in order. A caller drawing a
    /// calendar clips them to its range; an occurrence that started before <paramref name="from"/>
    /// is included so its visible remainder can be drawn rather than omitted.
    /// </summary>
    public static IEnumerable<WindowOccurrence> OccurrencesBetween(
        RateLimitWindowDefinition window,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        if (!IsWellFormed(window) || to <= from)
        {
            yield break;
        }

        if (window.IsOnce)
        {
            if (OnceOccurrence(window) is { } once && once.End > from && once.Start < to)
            {
                yield return once;
            }

            yield break;
        }

        TryResolveTimeZone(window.TimeZone, out var zone);
        var first = TimeZoneInfo.ConvertTime(from, zone).Date.AddDays(-1);
        var last = TimeZoneInfo.ConvertTime(to, zone).Date;

        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (TryBuildWeeklyOccurrence(window, zone, day, out var occurrence) &&
                occurrence.End > from &&
                occurrence.Start < to)
            {
                yield return occurrence;
            }
        }
    }

    /// <summary>The tier a rule enforces at <paramref name="now"/>, given its base tier and windows.</summary>
    public static ScheduleEvaluation Evaluate(
        RateLimitPolicy basePolicy,
        IReadOnlyList<RateLimitWindowDefinition>? windows,
        DateTimeOffset now)
    {
        if (windows is null || windows.Count == 0)
        {
            return new ScheduleEvaluation(basePolicy, null, null, null, Suspended: false);
        }

        RateLimitWindowDefinition? winner = null;
        WindowOccurrence winnerOccurrence = default;
        DateTimeOffset? nextTransition = null;

        foreach (var window in windows)
        {
            if (TryGetOccurrence(window, now, out var occurrence))
            {
                if (winner is null || Outranks(window, occurrence, winner, winnerOccurrence))
                {
                    winner = window;
                    winnerOccurrence = occurrence;
                }

                // Any active window's end is a moment at which the answer may change: if it is the
                // winner the base tier or another window takes over; if it is not, nothing visible
                // changes, and re-evaluating then is harmless.
                Consider(ref nextTransition, occurrence.End);
            }

            if (NextOccurrence(window, now) is { } next)
            {
                Consider(ref nextTransition, next.Start);
            }
        }

        if (winner is null)
        {
            return new ScheduleEvaluation(basePolicy, null, null, nextTransition, Suspended: false);
        }

        var until = winnerOccurrence.End == DateTimeOffset.MaxValue ? (DateTimeOffset?)null : winnerOccurrence.End;
        return new ScheduleEvaluation(winner.ToPolicy(), winner, until, nextTransition, winner.Suspend);
    }

    private static bool Outranks(
        RateLimitWindowDefinition candidate,
        WindowOccurrence candidateOccurrence,
        RateLimitWindowDefinition incumbent,
        WindowOccurrence incumbentOccurrence)
    {
        if (candidate.Rank != incumbent.Rank)
        {
            return candidate.Rank > incumbent.Rank;
        }

        // Same rank should not happen for a saved configuration (validation refuses same-kind
        // overlaps), but a stored set can predate a rule change; the later start wins so the
        // answer is at least deterministic and favours the more recently begun window.
        if (candidateOccurrence.Start != incumbentOccurrence.Start)
        {
            return candidateOccurrence.Start > incumbentOccurrence.Start;
        }

        return string.CompareOrdinal(candidate.Name, incumbent.Name) < 0;
    }

    private static void Consider(ref DateTimeOffset? next, DateTimeOffset candidate)
    {
        if (candidate == DateTimeOffset.MaxValue)
        {
            return;
        }

        if (next is null || candidate < next)
        {
            next = candidate;
        }
    }

    private static WindowOccurrence? OnceOccurrence(RateLimitWindowDefinition window)
    {
        if (window.From is not { } from)
        {
            return null;
        }

        var start = from;
        var end = window.Until ?? DateTimeOffset.MaxValue;

        if (window.ValidFrom is { } validFrom && start < validFrom)
        {
            return null;
        }

        if (window.ValidUntil is { } validUntil)
        {
            if (start >= validUntil)
            {
                return null;
            }

            if (end > validUntil)
            {
                end = validUntil;
            }
        }

        return end > start ? new WindowOccurrence(start, end) : null;
    }

    /// <summary>
    /// The occurrence that starts on <paramref name="localDay"/> in <paramref name="zone"/>, if the
    /// window starts on that weekday and its validity bounds allow it.
    /// </summary>
    private static bool TryBuildWeeklyOccurrence(
        RateLimitWindowDefinition window,
        TimeZoneInfo zone,
        DateTime localDay,
        out WindowOccurrence occurrence)
    {
        occurrence = default;

        var starts = false;
        foreach (var label in window.Days!)
        {
            if (TryParseDay(label, out var day) && day == localDay.DayOfWeek)
            {
                starts = true;
                break;
            }
        }

        if (!starts)
        {
            return false;
        }

        TryParseTime(window.Start, out var startTime);
        TryParseTime(window.End, out var endTime);

        var startLocal = localDay + startTime;
        var endLocal = endTime <= startTime
            ? localDay.AddDays(1) + endTime
            : localDay + endTime;

        var start = ToUtc(startLocal, zone);
        var end = ToUtc(endLocal, zone);

        if (end <= start)
        {
            return false;
        }

        if (window.ValidFrom is { } validFrom && start < validFrom)
        {
            return false;
        }

        if (window.ValidUntil is { } validUntil)
        {
            if (start >= validUntil)
            {
                return false;
            }

            if (end > validUntil)
            {
                end = validUntil;
            }
        }

        occurrence = new WindowOccurrence(start, end);
        return true;
    }

    /// <summary>
    /// A local wall-clock time as an instant. A time inside a daylight-saving gap does not exist,
    /// so it is moved forward to the first instant that does; an ambiguous time takes the zone's
    /// standard offset, which is what <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> returns.
    /// </summary>
    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        var offset = zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
}
