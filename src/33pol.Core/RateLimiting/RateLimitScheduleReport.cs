using Pol33.Core.Configuration;

namespace Pol33.Core.RateLimiting;

/// <summary>A tier as the schedule report shows it.</summary>
public sealed record ScheduleTier(int Rpm, int Burst, int MaxConcurrentStreams, bool Suspended)
{
    public static ScheduleTier From(RateLimitPolicy policy, bool suspended = false) =>
        new(policy.Rpm, policy.Burst, policy.MaxConcurrentStreams, suspended);
}

/// <summary>One window as the report describes it: its state at the reference instant, and why if it cannot run.</summary>
/// <param name="State">One of <c>active</c>, <c>upcoming</c>, <c>expired</c>, <c>invalid</c>.</param>
public sealed record ScheduleWindowStatus(
    string Name,
    string Kind,
    string State,
    DateTimeOffset? NextStartAt,
    DateTimeOffset? NextEndAt,
    string? Error);

/// <summary>One rule's standing at the reference instant.</summary>
public sealed record ScheduleRuleStatus(
    string Scope,
    string Target,
    ScheduleTier Base,
    ScheduleTier Effective,
    string? ActiveWindow,
    DateTimeOffset? ActiveUntil,
    DateTimeOffset? NextChangeAt,
    string? NextWindow,
    IReadOnlyList<ScheduleWindowStatus> Windows);

/// <summary>One span on the calendar: a window's occurrence, clipped to the requested range.</summary>
public sealed record ScheduleOccurrence(
    string Scope,
    string Target,
    string Window,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool ClippedStart,
    bool ClippedEnd,
    ScheduleTier Tier);

/// <summary>One moment at which a rule's effective tier changes.</summary>
public sealed record ScheduleTransition(
    DateTimeOffset At,
    string Scope,
    string Target,
    string? Window,
    ScheduleTier From,
    ScheduleTier To);

/// <summary>
/// What every rule enforces at one instant, what it will enforce over a range, and when each
/// change happens. Served from <c>GET /admin/api/rate-limits/schedule</c>.
/// </summary>
public sealed record RateLimitScheduleReport(
    DateTimeOffset At,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<ScheduleRuleStatus> Rules,
    IReadOnlyList<ScheduleOccurrence> Occurrences,
    IReadOnlyList<ScheduleTransition> Transitions,
    int TransitionsTotal,
    bool TransitionsTruncated,
    int OccurrencesTotal = 0,
    bool OccurrencesTruncated = false);

/// <summary>Builds <see cref="RateLimitScheduleReport"/> from the stored configuration.</summary>
public static class RateLimitScheduleReportBuilder
{
    public const int MaxTransitions = 500;

    /// <summary>
    /// Ceiling on the calendar spans one report may carry.
    /// </summary>
    /// <remarks>
    /// Transitions were capped from the start and occurrences were not, though they are built in the
    /// same loop and grow faster: a weekly window yields one occurrence per matching day, so the list
    /// scales as rules × windows × days. At the configured ceilings — 2,000 rules, 16 windows each,
    /// and the 62-day range the endpoint allows — that is nearly two million records serialised into
    /// one response, from a request an operator can repeat. Capped, with the total reported, so a
    /// console can say the calendar was trimmed rather than quietly drawing a partial one.
    /// </remarks>
    public const int MaxOccurrences = 2_000;

    public static RateLimitScheduleReport Build(
        IReadOnlyList<RateLimitRuleDefinition> rules,
        DateTimeOffset at,
        DateTimeOffset from,
        DateTimeOffset to,
        int take)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var statuses = new List<ScheduleRuleStatus>(rules.Count);
        var occurrences = new List<ScheduleOccurrence>();
        var transitions = new List<ScheduleTransition>();

        foreach (var rule in rules)
        {
            var basePolicy = rule.ToPolicy();
            var windows = rule.Windows;
            var evaluation = RateLimitScheduleEvaluator.Evaluate(basePolicy, windows, at);

            statuses.Add(new ScheduleRuleStatus(
                rule.Scope,
                rule.TargetKey,
                ScheduleTier.From(basePolicy),
                ScheduleTier.From(evaluation.Effective, evaluation.Suspended),
                evaluation.ActiveWindow?.Name,
                evaluation.ActiveUntil,
                evaluation.NextTransition,
                NextWindowName(windows, evaluation, at),
                windows.Select(w => DescribeWindow(w, at)).ToArray()));

            if (windows.Count == 0)
            {
                continue;
            }

            foreach (var window in windows)
            {
                foreach (var occurrence in RateLimitScheduleEvaluator.OccurrencesBetween(window, from, to))
                {
                    var start = occurrence.Start < from ? from : occurrence.Start;
                    var end = occurrence.End > to ? to : occurrence.End;
                    occurrences.Add(new ScheduleOccurrence(
                        rule.Scope,
                        rule.TargetKey,
                        window.Name,
                        start,
                        end,
                        ClippedStart: occurrence.Start < from,
                        ClippedEnd: occurrence.End > to,
                        ScheduleTier.From(window.ToPolicy(), window.Suspend)));
                }
            }

            transitions.AddRange(Transitions(rule, basePolicy, windows, from, to));
        }

        occurrences.Sort((a, b) => a.Start.CompareTo(b.Start));
        transitions.Sort((a, b) => a.At.CompareTo(b.At));

        var limit = Math.Clamp(take, 1, MaxTransitions);
        var truncated = transitions.Count > limit;

        // Both lists are trimmed from the front, so what survives is the soonest of each — the part a
        // calendar draws first and the part an operator is actually looking for.
        var occurrencesTruncated = occurrences.Count > MaxOccurrences;

        return new RateLimitScheduleReport(
            at,
            from,
            to,
            statuses,
            occurrencesTruncated ? occurrences.Take(MaxOccurrences).ToArray() : occurrences,
            truncated ? transitions.Take(limit).ToArray() : transitions,
            transitions.Count,
            truncated,
            occurrences.Count,
            occurrencesTruncated);
    }

    private static ScheduleWindowStatus DescribeWindow(RateLimitWindowDefinition window, DateTimeOffset at)
    {
        var error = RateLimitScheduleEvaluator.Describe(window);
        if (error is not null)
        {
            return new ScheduleWindowStatus(window.Name, window.Kind, "invalid", null, null, error);
        }

        if (RateLimitScheduleEvaluator.TryGetOccurrence(window, at, out var current))
        {
            var end = current.End == DateTimeOffset.MaxValue ? (DateTimeOffset?)null : current.End;
            return new ScheduleWindowStatus(window.Name, window.Kind, "active", current.Start, end, null);
        }

        var next = RateLimitScheduleEvaluator.NextOccurrence(window, at);
        if (next is { } n)
        {
            var end = n.End == DateTimeOffset.MaxValue ? (DateTimeOffset?)null : n.End;
            return new ScheduleWindowStatus(window.Name, window.Kind, "upcoming", n.Start, end, null);
        }

        return new ScheduleWindowStatus(window.Name, window.Kind, "expired", null, null, null);
    }

    /// <summary>The window that takes over at the next transition, when the next transition is a start.</summary>
    private static string? NextWindowName(
        IReadOnlyList<RateLimitWindowDefinition> windows,
        ScheduleEvaluation evaluation,
        DateTimeOffset at)
    {
        if (evaluation.NextTransition is not { } next)
        {
            return null;
        }

        var after = RateLimitScheduleEvaluator.Evaluate(RateLimitPolicy.Unlimited, windows, next);
        _ = at;
        return after.ActiveWindow?.Name;
    }

    /// <summary>
    /// The moments in <c>[from, to)</c> at which the rule's effective tier changes, found by
    /// evaluating the rule at every occurrence boundary and keeping the ones where the answer moved.
    /// </summary>
    private static IEnumerable<ScheduleTransition> Transitions(
        RateLimitRuleDefinition rule,
        RateLimitPolicy basePolicy,
        IReadOnlyList<RateLimitWindowDefinition> windows,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var boundaries = new SortedSet<DateTimeOffset>();
        foreach (var window in windows)
        {
            foreach (var occurrence in RateLimitScheduleEvaluator.OccurrencesBetween(window, from, to))
            {
                if (occurrence.Start > from && occurrence.Start < to)
                {
                    boundaries.Add(occurrence.Start);
                }

                if (occurrence.End > from && occurrence.End < to)
                {
                    boundaries.Add(occurrence.End);
                }
            }
        }

        var previous = RateLimitScheduleEvaluator.Evaluate(basePolicy, windows, from);
        foreach (var boundary in boundaries)
        {
            var current = RateLimitScheduleEvaluator.Evaluate(basePolicy, windows, boundary);
            if (current.Effective != previous.Effective ||
                current.Suspended != previous.Suspended ||
                current.ActiveWindow?.Name != previous.ActiveWindow?.Name)
            {
                yield return new ScheduleTransition(
                    boundary,
                    rule.Scope,
                    rule.TargetKey,
                    current.ActiveWindow?.Name,
                    ScheduleTier.From(previous.Effective, previous.Suspended),
                    ScheduleTier.From(current.Effective, current.Suspended));
            }

            previous = current;
        }
    }
}

/// <summary>
/// What an operator sees while composing a window, before committing it: whether it is valid, when
/// it next runs, and how it stands against the rule's other windows.
/// </summary>
public sealed record RateLimitWindowPreview(
    bool Valid,
    string? Error,
    DateTimeOffset? NextStartAt,
    DateTimeOffset? NextEndAt,
    bool ActiveNow,
    IReadOnlyList<string> Overlaps,
    IReadOnlyList<string> OutrankedBy,
    IReadOnlyList<string> Outranks);

public static class RateLimitWindowPreviewBuilder
{
    /// <param name="rule">The rule with every window it would have, the candidate included.</param>
    /// <param name="candidateName">Which of those windows is being composed.</param>
    public static RateLimitWindowPreview Build(RateLimitRuleDefinition rule, string candidateName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var windows = rule.Windows;

        // The size gate comes before anything is computed. Every other refusal below still describes
        // the rule in full — a composer needs to be told *why* its window is invalid and what it
        // clashes with — but the overlap scan is quadratic in the window count, and the window
        // ceiling is itself one of the rules validation enforces. So a set past that ceiling is the
        // one case where continuing would mean running a scan whose size the caller chose, and it is
        // answered here with the refusal a save would give it and no work at all.
        if (windows.Count > RateLimitConfigValidation.MaxWindowsPerRule)
        {
            return new RateLimitWindowPreview(
                false,
                $"A rule may not have more than {RateLimitConfigValidation.MaxWindowsPerRule} windows.",
                null,
                null,
                false,
                [],
                [],
                []);
        }

        var candidate = windows.FirstOrDefault(w =>
            string.Equals(w.Name, candidateName, StringComparison.OrdinalIgnoreCase));

        if (candidate is null)
        {
            return new RateLimitWindowPreview(false, "The window to preview is not in the rule.", null, null, false, [], [], []);
        }

        string? error = null;
        if (!RateLimitConfigValidation.TryValidateRules([rule], out var validationError))
        {
            error = validationError;
        }

        var overlaps = OverlapsWith(windows, candidateName);

        var others = windows.Where(w => !ReferenceEquals(w, candidate)).ToArray();
        var outrankedBy = others.Where(w => w.Rank > candidate.Rank).Select(w => w.Name).ToArray();
        var outranks = others.Where(w => w.Rank < candidate.Rank).Select(w => w.Name).ToArray();

        var activeNow = RateLimitScheduleEvaluator.TryGetOccurrence(candidate, now, out var current);
        WindowOccurrence? next = activeNow ? current : RateLimitScheduleEvaluator.NextOccurrence(candidate, now);

        return new RateLimitWindowPreview(
            error is null,
            error,
            next?.Start,
            next is { } n && n.End != DateTimeOffset.MaxValue ? n.End : null,
            activeNow,
            overlaps,
            outrankedBy,
            outranks);
    }

    /// <summary>The other windows the named one can be active alongside without a clear winner.</summary>
    private static string[] OverlapsWith(IReadOnlyList<RateLimitWindowDefinition> windows, string candidateName) =>
        [.. RateLimitConfigValidation
            .FindWindowOverlaps(windows)
            .Where(pair => Involves(pair, candidateName))
            .Select(pair => string.Equals(pair.First, candidateName, StringComparison.OrdinalIgnoreCase) ? pair.Second : pair.First)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static bool Involves((string First, string Second) pair, string name) =>
        string.Equals(pair.First, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pair.Second, name, StringComparison.OrdinalIgnoreCase);
}
