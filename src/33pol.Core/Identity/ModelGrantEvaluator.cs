namespace Pol33.Core.Identity;

public static class ModelGrantEvaluator
{
    public static bool IsModelAllowed(IReadOnlyList<ModelGrantRecord> grants, string canonicalModelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalModelId);

        if (grants.Count == 0)
        {
            return true;
        }

        return MatchesAllowGrant(grants.Select(g => (g.Effect, g.ModelPattern)), canonicalModelId);
    }

    public static bool IsModelAllowed(
        IReadOnlyList<ModelGrantRecord> tenantGrants,
        IReadOnlyList<ApiKeyModelGrantRecord> apiKeyGrants,
        string canonicalModelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalModelId);

        if (!IsModelAllowed(tenantGrants, canonicalModelId))
        {
            return false;
        }

        if (apiKeyGrants.Count == 0)
        {
            return false;
        }

        return MatchesAllowGrant(
            apiKeyGrants.Select(g => (g.Effect, g.ModelPattern)),
            canonicalModelId);
    }

    private static bool MatchesAllowGrant(
        IEnumerable<(GrantEffect Effect, string ModelPattern)> grants,
        string canonicalModelId) =>
        grants.Any(g =>
            g.Effect == GrantEffect.Allow
            && string.Equals(g.ModelPattern, canonicalModelId, StringComparison.OrdinalIgnoreCase));
}
