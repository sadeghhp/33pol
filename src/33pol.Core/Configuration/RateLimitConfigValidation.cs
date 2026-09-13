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

            // Zero rpm and zero streams is a rule that enforces nothing. Accepting it would let an
            // operator believe a limit is in place while every request walks past it.
            var tier = new RateLimitTierOptions
            {
                Rpm = rule.Rpm,
                Burst = rule.Burst,
                MaxConcurrentStreams = rule.MaxConcurrentStreams,
            };

            if (tier.EnforcesNothing)
            {
                error = $"rule '{rule.Identity}' enforces nothing; set rpm or maxConcurrentStreams above zero, or remove it.";
                return false;
            }

            // Neither branch below sees a negative rpm: the tier check runs only above zero and the
            // concurrency-only check only at zero, so -50 with a burst of 100 used to be stored as
            // a 50-token bucket refilling at the floor rate, and reported as a negative limit.
            if (rule.Rpm < 0)
            {
                error = $"rule '{rule.Identity}' has a negative rpm; use 0 to leave the rate unlimited by this rule.";
                return false;
            }

            // A tenant override with rpm 0 inherits its plan's (or the default's) rate and applies
            // only its stream cap. A burst alongside that zero would have no rate to refill it and
            // no tier to belong to, so it is refused rather than silently dropped.
            if (rule.Rpm == 0 &&
                rule.Burst != 0 &&
                string.Equals(rule.Scope, RateLimitScopeNames.Tenant, StringComparison.OrdinalIgnoreCase))
            {
                error = $"rule '{rule.Identity}' inherits the plan or default rate when rpm is 0; set burst to 0 as well.";
                return false;
            }

            // Scoped rules may leave rpm at zero to cap only concurrency, so the shared tier check
            // (which floors rpm at 1) is applied only when the rule limits the rate at all.
            if (rule.Rpm > 0 && !TryValidateTier(tier, $"rule '{rule.Identity}'", out error))
            {
                return false;
            }

            if (rule.Rpm == 0 &&
                (rule.Burst is < MinBurst or > MaxBurst ||
                 rule.MaxConcurrentStreams is < MinMaxConcurrentStreams or > MaxMaxConcurrentStreams))
            {
                error = $"rule '{rule.Identity}' has a burst or maxConcurrentStreams outside the allowed range.";
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
            error = $"rule '{rule.Identity}': windows '{first}' and '{second}' are the same kind and can be active at the same time; give one a different span or a priority.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The pairs of same-kind windows that can be active at the same instant. Two <c>once</c>
    /// windows overlap when their spans intersect; two <c>weekly</c> windows when any minute of the
    /// week falls in both. Windows with an explicit priority are exempt: the operator has said
    /// which wins.
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
                if (a is null || b is null || a.Priority is not null || b.Priority is not null)
                {
                    continue;
                }

                if (!string.Equals(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!RateLimitScheduleEvaluator.IsWellFormed(a) || !RateLimitScheduleEvaluator.IsWellFormed(b))
                {
                    continue;
                }

                if (a.IsOnce ? OnceOverlap(a, b) : WeeklyOverlap(a, b))
                {
                    result.Add((a.Name, b.Name));
                }
            }
        }

        return result;
    }

    private static bool OnceOverlap(RateLimitWindowDefinition a, RateLimitWindowDefinition b)
    {
        var aEnd = a.Until ?? DateTimeOffset.MaxValue;
        var bEnd = b.Until ?? DateTimeOffset.MaxValue;
        return a.From < bEnd && b.From < aEnd;
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
        foreach (var x in MinuteOfWeekSpans(a))
        {
            foreach (var y in MinuteOfWeekSpans(b))
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
        error = null;
        var tier = new RateLimitTierOptions
        {
            Rpm = window.Rpm,
            Burst = window.Burst,
            MaxConcurrentStreams = window.MaxConcurrentStreams,
        };

        if (tier.EnforcesNothing)
        {
            error = $"{label} enforces nothing while active; set rpm or maxConcurrentStreams above zero, or mark it as suspending the rule.";
            return false;
        }

        if (window.Rpm < 0)
        {
            error = $"{label} has a negative rpm; use 0 to leave the rate unlimited by this rule.";
            return false;
        }

        if (window.Rpm == 0 &&
            window.Burst != 0 &&
            string.Equals(rule.Scope, RateLimitScopeNames.Tenant, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{label} inherits the plan or default rate when rpm is 0; set burst to 0 as well.";
            return false;
        }

        if (window.Rpm > 0 && !TryValidateTier(tier, label, out error))
        {
            return false;
        }

        if (window.Rpm == 0 &&
            (window.Burst is < MinBurst or > MaxBurst ||
             window.MaxConcurrentStreams is < MinMaxConcurrentStreams or > MaxMaxConcurrentStreams))
        {
            error = $"{label} has a burst or maxConcurrentStreams outside the allowed range.";
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
