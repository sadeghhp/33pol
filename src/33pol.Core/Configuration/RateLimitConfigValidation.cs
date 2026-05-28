using System.Text.RegularExpressions;

namespace Pol33.Core.Configuration;

/// <summary>
/// Validates admin-managed rate-limit tiers (default + plans). Tenants are out of MVP scope.
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

        var trimmed = slug.Trim();
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
