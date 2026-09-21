using System.Text.RegularExpressions;
using Pol33.Core.RateLimiting;

namespace Pol33.Core.Configuration;

/// <summary>
/// Validates admin-managed rate-limit configuration: the default tier, the per-plan tiers, and the
/// scoped rules.
/// </summary>
public static partial class RateLimitConfigValidation
{
    public const int MinRpm = 1;
    public const int MaxRpm = 1_000_000;
    public const int MinBurst = 0;
    public const int MaxBurst = 1_000_000;
    public const int MinMaxConcurrentStreams = 0;
    public const int MaxMaxConcurrentStreams = 10_000;
    public const int MaxPlanSlugLength = 64;
    public const int MaxTargetKeyLength = 256;

    /// <summary>
    /// Ceiling on how many scoped rules may be configured. The whole set is loaded into the config
    /// snapshot and rebuilt on every admin write, so it is a working set rather than a data store;
    /// past a few thousand rules the answer is a per-plan tier, not more rows.
    /// </summary>
    public const int MaxRules = 2_000;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]*$")]
    private static partial Regex PlanSlugPattern();

    public static bool TryValidate(
        RateLimitTierOptions? defaultTier,
        IReadOnlyDictionary<string, RateLimitTierOptions>? plans,
        out string? error)
    {
        error = null;

        if (defaultTier is null)
        {
            error = "default is required.";
            return false;
        }

        if (!TryValidateTier(defaultTier, "default", out error))
        {
            return false;
        }

        if (plans is null)
        {
            error = "plans is required.";
            return false;
        }

        foreach (var (slug, tier) in plans)
        {
            if (!TryValidatePlanSlug(slug, out error))
            {
                return false;
            }

            if (tier is null)
            {
                error = $"plans['{slug}'] is required.";
                return false;
            }

            if (!TryValidateTier(tier, $"plans['{slug}']", out error))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates a set of scoped rules: known scope, well-formed target for that scope, sane tier,
    /// and no two rules claiming the same (scope, target).
    /// </summary>
    public static bool TryValidateRules(IReadOnlyList<RateLimitRuleDefinition>? rules, out string? error)
    {
        error = null;

        if (rules is null)
        {
            return true;
        }

        if (rules.Count > MaxRules)
        {
            error = $"rules may not exceed {MaxRules} entries.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (rule is null)
            {
                error = "rules entries cannot be null.";
                return false;
            }

            if (!RateLimitScopeNames.IsKnown(rule.Scope))
            {
                error = $"rule scope '{rule.Scope}' is not one of: {string.Join(", ", RateLimitScopeNames.All)}.";
                return false;
            }

            if (!TryValidateTarget(rule, out error))
            {
                return false;
            }

            var tier = new RateLimitTierOptions
            {
                Rpm = rule.Rpm,
                Burst = rule.Burst,
                MaxConcurrentStreams = rule.MaxConcurrentStreams,
            };

            if (!TryValidateTierShape(tier, rule.Scope, $"rule '{rule.Identity}'", out error))
            {
                return false;
            }

            if (!TryValidateSchedule(rule, out error))
            {
                return false;
            }

            if (!seen.Add(rule.Identity))
            {
                // Silently keeping the last one would make the applied configuration depend on the
                // order the client happened to serialise its list in.
                error = $"rule '{rule.Identity}' is defined more than once.";
                return false;
            }
        }

        return true;
    }

    public const int MaxWindowsPerRule = 16;
    public const int MaxWindowNameLength = 64;
    public const int MinPriority = 0;
    public const int MaxPriority = 1000;

    /// <summary>
    /// Validates a rule's schedule: well-formed windows with unique names, tiers that pass the same
    /// checks as the rule's own, one time zone per rule, and no two windows of the same kind that
    /// can be active at once. A null schedule is "unspecified" and always valid.
    /// </summary>
    public static bool TryValidateSchedule(RateLimitRuleDefinition rule, out string? error)
    {
        error = null;
        var windows = rule.Schedule;
        if (windows is null || windows.Count == 0)
        {
            return true;
        }

        if (windows.Count > MaxWindowsPerRule)
        {
            error = $"rule '{rule.Identity}' may not have more than {MaxWindowsPerRule} windows.";
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? zone = null;

        foreach (var window in windows)
        {
            if (window is null)
            {
                error = $"rule '{rule.Identity}' has a null window.";
                return false;
            }

            var name = window.Name;
            if (string.IsNullOrWhiteSpace(name) || name.Length != name.Trim().Length || name.Length > MaxWindowNameLength)
            {
                error = $"rule '{rule.Identity}': every window needs a name of at most {MaxWindowNameLength} characters with no surrounding whitespace.";
                return false;
            }

            var label = $"rule '{rule.Identity}' window '{name}'";

            if (!names.Add(name))
            {
                error = $"{label} is defined more than once.";
                return false;
            }

            if (!RateLimitWindowKinds.IsKnown(window.Kind))
            {
                error = $"{label}: kind must be '{RateLimitWindowKinds.Once}' or '{RateLimitWindowKinds.Weekly}'.";
                return false;
            }

            if (window.Priority is { } priority && (priority < MinPriority || priority > MaxPriority))
            {
                error = $"{label}: priority must be between {MinPriority} and {MaxPriority}.";
                return false;
            }

            var shape = RateLimitScheduleEvaluator.Describe(window);
            if (shape is not null)
            {
                error = $"{label}: {shape}.";
                return false;
            }

            if (window.IsWeekly)
            {
                // One zone per rule keeps the overlap check below a static question. Two zones
                // whose offsets move on different dates can overlap on some weeks and not others,
                // and a rule that is valid in March and invalid in October is not a rule anyone
                // can reason about.
                var windowZone = string.IsNullOrWhiteSpace(window.TimeZone) ? "UTC" : window.TimeZone.Trim();
                if (zone is null)
                {
                    zone = windowZone;
                }
                else if (!string.Equals(zone, windowZone, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"{label}: every weekly window on a rule must use the same time zone ('{zone}').";
                    return false;
                }
            }

            if (!window.Suspend && !TryValidateWindowTier(rule, window, label, out error))
            {
                return false;
            }
        }

        var overlaps = FindWindowOverlaps(windows);
        if (overlaps.Count > 0)
        {
            var (first, second) = overlaps[0];
            error = $"rule '{rule.Identity}': windows '{first}' and '{second}' are the same kind and can be active at the same time; give one a different span or a higher priority than the other.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The pairs of same-kind windows that can be active at the same instant without a clear
    /// winner. Two <c>once</c> windows overlap when their spans intersect; two <c>weekly</c>
    /// windows when any minute of the week falls in both. A pair whose ranks differ is exempt: the
    /// operator has said which wins. Equal ranks — both unset, or both set to the same number —
    /// are not, because the evaluator would then pick by start time and name, which nobody asked for.
    /// </summary>
    public static IReadOnlyList<(string First, string Second)> FindWindowOverlaps(
        IReadOnlyList<RateLimitWindowDefinition> windows)
    {
        var result = new List<(string, string)>();

        for (var i = 0; i < windows.Count; i++)
        {
            for (var j = i + 1; j < windows.Count; j++)
            {
                var a = windows[i];
                var b = windows[j];
                if (a is null || b is null)
                {
                    continue;
                }

                // Rank first, kind second. A differing rank settles the pair whatever their kinds
                // are, and testing kind first meant a once window and a weekly one were never
                // compared at all — safe only while their default ranks differ. Priority is
                // operator-settable, so two windows of different kinds can carry the same rank, be
                // active together, and fall through to the evaluator's start-time-then-name
                // tie-break: precisely the arbitrary answer the equal-rank refusal exists to prevent.
                if (a.Rank != b.Rank)
                {
                    continue;
                }

                if (!RateLimitScheduleEvaluator.IsWellFormed(a) || !RateLimitScheduleEvaluator.IsWellFormed(b))
                {
                    continue;
                }

                if (Overlap(a, b))
                {
                    result.Add((a.Name, b.Name));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Whether two equal-ranked windows can be active at the same instant.
    /// </summary>
    /// <remarks>
    /// Two windows of one kind are compared on that kind's own axis — instants for <c>once</c>,
    /// minutes-of-week for <c>weekly</c>. A mixed pair has no shared axis, so it is answered
    /// conservatively: a weekly window recurs indefinitely within its validity bounds, so if the once
    /// window's span meets those bounds at all there is a week in which the two coincide. Refusing
    /// the pair costs the operator one <c>priority</c> to say which should win; accepting it leaves
    /// the answer to window naming.
    /// </remarks>
    private static bool Overlap(RateLimitWindowDefinition a, RateLimitWindowDefinition b)
    {
        if (string.Equals(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return a.IsOnce ? OnceOverlap(a, b) : WeeklyOverlap(a, b);
        }

        var (once, weekly) = a.IsOnce ? (a, b) : (b, a);
        var (onceStart, onceEnd) = OnceSpan(once);
        if (onceEnd <= onceStart)
        {
            return false;
        }

        var weeklyFrom = weekly.ValidFrom ?? DateTimeOffset.MinValue;
        var weeklyUntil = weekly.ValidUntil ?? DateTimeOffset.MaxValue;
        return onceStart < weeklyUntil && weeklyFrom < onceEnd;
    }

    private static bool OnceOverlap(RateLimitWindowDefinition a, RateLimitWindowDefinition b)
    {
        // The span a once window actually occupies: its own bounds, narrowed by its validity
        // bounds the same way the evaluator narrows them, so a window whose validity ends before
        // the other begins is not reported as a clash it can never have.
        var (aStart, aEnd) = OnceSpan(a);
        var (bStart, bEnd) = OnceSpan(b);
        return aStart < bEnd && bStart < aEnd;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) OnceSpan(RateLimitWindowDefinition window)
    {
        var start = window.From ?? DateTimeOffset.MinValue;
        var end = window.Until ?? DateTimeOffset.MaxValue;
        if (window.ValidUntil is { } validUntil && validUntil < end)
        {
            end = validUntil;
        }

        // A start before ValidFrom means the occurrence does not happen at all (the evaluator
        // drops it), so the window occupies nothing.
        if (window.ValidFrom is { } validFrom && start < validFrom)
        {
            return (start, start);
        }

        return (start, end);
    }

    private static bool WeeklyOverlap(RateLimitWindowDefinition a, RateLimitWindowDefinition b)
    {
        // Bounded windows whose validity ranges never meet cannot overlap whatever their weeks say.
        var aFrom = a.ValidFrom ?? DateTimeOffset.MinValue;
        var aUntil = a.ValidUntil ?? DateTimeOffset.MaxValue;
        var bFrom = b.ValidFrom ?? DateTimeOffset.MinValue;
        var bUntil = b.ValidUntil ?? DateTimeOffset.MaxValue;
        if (aFrom >= bUntil || bFrom >= aUntil)
        {
            return false;
        }

        // Zones are equal by construction on a saved rule (TryValidateSchedule enforces it), so
        // minute-of-week intervals are comparable directly. Across different zones the answer is
        // computed conservatively in the same local frame.
        // Materialised once. MinuteOfWeekSpans is an iterator that re-parses the window on every
        // enumeration, and enumerating the inner one afresh for each outer span made that parse
        // quadratic. Both lists are small by construction: a well-formed window has at most seven
        // days, so at most fourteen spans.
        var spansOfB = MinuteOfWeekSpans(b).ToArray();
        foreach (var x in MinuteOfWeekSpans(a))
        {
            foreach (var y in spansOfB)
            {
                if (x.Start < y.End && y.Start < x.End)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A weekly window as spans of minutes since Monday 00:00, wrapped at the week's end.</summary>
    private static IEnumerable<(int Start, int End)> MinuteOfWeekSpans(RateLimitWindowDefinition window)
    {
        const int MinutesPerDay = 24 * 60;
        const int MinutesPerWeek = 7 * MinutesPerDay;

        RateLimitScheduleEvaluator.TryParseTime(window.Start, out var start);
        RateLimitScheduleEvaluator.TryParseTime(window.End, out var end);
        var length = end <= start ? (TimeSpan.FromDays(1) - start + end) : (end - start);

        foreach (var label in window.Days!)
        {
            RateLimitScheduleEvaluator.TryParseDay(label, out var day);
            var dayIndex = ((int)day + 6) % 7; // Monday = 0
            var from = dayIndex * MinutesPerDay + (int)start.TotalMinutes;
            var to = from + (int)length.TotalMinutes;
            if (to <= MinutesPerWeek)
            {
                yield return (from, to);
            }
            else
            {
                yield return (from, MinutesPerWeek);
                yield return (0, to - MinutesPerWeek);
            }
        }
    }

    private static bool TryValidateWindowTier(
        RateLimitRuleDefinition rule,
        RateLimitWindowDefinition window,
        string label,
        out string? error)
    {
        var tier = new RateLimitTierOptions
        {
            Rpm = window.Rpm,
            Burst = window.Burst,
            MaxConcurrentStreams = window.MaxConcurrentStreams,
        };

        // The same shape rules as the rule's own base tier. A window is a tier that applies for a
        // span of time, so a shape the rule itself may not hold must not become reachable by
        // scheduling it — which is what a second, laxer copy of these checks produced.
        return TryValidateTierShape(tier, rule.Scope, label, out error);
    }

    /// <summary>
    /// The shape a tier must have to mean what it says, applied identically to a rule's base tier and
    /// to every window on it.
    /// </summary>
    /// <remarks>
    /// <para>Three things are checked here that a plain range check cannot see.</para>
    ///
    /// <para><b>A zero rpm carries no burst.</b> Zero rpm is the documented "this rule does not limit
    /// the request rate" value, so there is no rate to refill a burst with. Accepting the pair stored
    /// a bucket of <c>burst</c> tokens refilling at the engine's floor of one token a minute, so a
    /// scope an operator had marked as rate-unlimited was in fact held to one request per minute once
    /// the burst was spent. The refusal used to apply to the <c>tenant</c> scope alone, though every
    /// scope reads a zero rpm the same way.</para>
    ///
    /// <para><b>A rate-only scope carries no stream cap.</b> <c>auth_failure</c> is evaluated by a
    /// limiter that only ever debits a token bucket, so a stream cap on it is inert — and a rule
    /// carrying nothing but a stream cap passes the "enforces something" test while leaving the
    /// gateway on the <em>default</em> tier for credential guessing, which is far wider than the
    /// purpose-built one. Refused rather than silently ignored.</para>
    ///
    /// <para><b>Order matters.</b> The negative-rpm test runs first so <c>-50</c> is reported as a
    /// negative rate rather than as a tier that enforces nothing, and the zero-rpm-with-burst test
    /// runs before it so the operator is told which of the two numbers to change.</para>
    /// </remarks>
    private static bool TryValidateTierShape(
        RateLimitTierOptions tier,
        string scope,
        string label,
        out string? error)
    {
        error = null;

        if (tier.Rpm < 0)
        {
            error = $"{label} has a negative rpm; use 0 to leave the rate unlimited by this rule.";
            return false;
        }

        if (tier.Rpm == 0 && tier.Burst != 0)
        {
            error =
                $"{label} has an rpm of 0, which leaves the rate unlimited by this rule, so its burst "
                + "has no rate to refill it; set burst to 0 as well.";
            return false;
        }

        if (tier.Rpm == 0 && RateLimitScopeNames.IsRateOnly(scope))
        {
            error =
                $"{label} is in the '{scope}' scope, which limits the request rate only; set rpm above "
                + "zero or remove the rule.";
            return false;
        }

        if (tier.MaxConcurrentStreams != 0 && RateLimitScopeNames.IsRateOnly(scope))
        {
            error =
                $"{label} is in the '{scope}' scope, which limits the request rate only; "
                + "maxConcurrentStreams has no effect there and must be 0.";
            return false;
        }

        // Zero rpm and zero streams is a tier that enforces nothing. Accepting it would let an
        // operator believe a limit is in place while every request walks past it.
        if (tier.EnforcesNothing)
        {
            error = $"{label} enforces nothing; set rpm or maxConcurrentStreams above zero, or remove it.";
            return false;
        }

        // Scoped rules may leave rpm at zero to cap only concurrency, so the shared tier check
        // (which floors rpm at 1) is applied only when the tier limits the rate at all.
        if (tier.Rpm > 0)
        {
            return TryValidateTier(tier, label, out error);
        }

        if (tier.MaxConcurrentStreams is < MinMaxConcurrentStreams or > MaxMaxConcurrentStreams)
        {
            error = $"{label} has a maxConcurrentStreams outside the allowed range.";
            return false;
        }

        return true;
    }

    private static bool TryValidateTarget(RateLimitRuleDefinition rule, out string? error)
    {
        error = null;
        var target = rule.TargetKey;

        if (string.IsNullOrWhiteSpace(target))
        {
            error = $"rule target for scope '{rule.Scope}' cannot be empty.";
            return false;
        }

        if (target.Length != target.Trim().Length)
        {
            // Stored verbatim and matched verbatim, so " gpt-4" would be a rule that can never fire.
            error = $"rule target '{target}' must not have leading or trailing whitespace.";
            return false;
        }

        if (target.Length > MaxTargetKeyLength)
        {
            error = $"rule target '{target}' exceeds {MaxTargetKeyLength} characters.";
            return false;
        }

        if (RateLimitScopeNames.IsSingleton(rule.Scope))
        {
            if (target != RateLimitScopeNames.SingletonTarget)
            {
                error = $"scope '{rule.Scope}' has a single partition; its target must be '{RateLimitScopeNames.SingletonTarget}'.";
                return false;
            }

            return true;
        }

        if (RateLimitScopeNames.IsPair(rule.Scope) &&
            !RateLimitKeys.TrySplitPair(target, out _, out _))
        {
            error =
                $"scope '{rule.Scope}' targets a pair; write it as 'subject{RateLimitKeys.PairSeparator}model' with exactly one separator.";
            return false;
        }

        if (!RateLimitScopeNames.IsPair(rule.Scope) &&
            target.IndexOf(RateLimitKeys.PairSeparator) >= 0)
        {
            error = $"rule target '{target}' must not contain '{RateLimitKeys.PairSeparator}' for scope '{rule.Scope}'.";
            return false;
        }

        return true;
    }

    public static bool TryValidateTier(RateLimitTierOptions tier, string path, out string? error)
    {
        error = null;

        if (tier.Rpm < MinRpm || tier.Rpm > MaxRpm)
        {
            error = $"{path}.rpm must be between {MinRpm} and {MaxRpm}.";
            return false;
        }

        if (tier.Burst < MinBurst || tier.Burst > MaxBurst)
        {
            error = $"{path}.burst must be between {MinBurst} and {MaxBurst}.";
            return false;
        }

        if (tier.MaxConcurrentStreams < MinMaxConcurrentStreams ||
            tier.MaxConcurrentStreams > MaxMaxConcurrentStreams)
        {
            error =
                $"{path}.maxConcurrentStreams must be between {MinMaxConcurrentStreams} and {MaxMaxConcurrentStreams}.";
            return false;
        }

        return true;
    }

    public static bool TryValidatePlanSlug(string? slug, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(slug))
        {
            error = "plans keys cannot be empty.";
            return false;
        }

        // Callers persist the key exactly as received, so validating a trimmed copy let " pro" through
        // and it then never matched a tenant whose plan is "pro".
        if (slug.Length != slug.Trim().Length)
        {
            error = $"plan slug '{slug}' must not have leading or trailing whitespace.";
            return false;
        }

        var trimmed = slug;
        if (trimmed.Length > MaxPlanSlugLength)
        {
            error = $"plan slug '{trimmed}' exceeds {MaxPlanSlugLength} characters.";
            return false;
        }

        if (!PlanSlugPattern().IsMatch(trimmed))
        {
            error =
                $"plan slug '{trimmed}' is invalid; use letters, digits, hyphen, or underscore and start with a letter.";
            return false;
        }

        return true;
    }
}
