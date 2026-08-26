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
