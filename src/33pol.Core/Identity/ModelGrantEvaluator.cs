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

    /// <summary>
    /// Evaluates one grant list. An explicit <see cref="GrantEffect.Deny"/> for the model wins over
    /// any <see cref="GrantEffect.Allow"/>.
    /// </summary>
    /// <remarks>
    /// Deny used to be inert: only Allow entries were examined, so a Deny alongside an Allow for the
    /// same model changed nothing. The admin API never writes Deny, which kept it from being an
    /// active hole — but the effect is persisted, so anything seeding grants outside that API
    /// (GitOps, a migration, a future UI) would have had its denials silently ignored. Deny-wins is
    /// the only safe reading of an authorization rule.
    /// </remarks>
    private static bool MatchesAllowGrant(
        IEnumerable<(GrantEffect Effect, string ModelPattern)> grants,
        string canonicalModelId)
    {
        var allowed = false;

        foreach (var (effect, pattern) in grants)
        {
            if (!string.Equals(pattern, canonicalModelId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (effect == GrantEffect.Deny)
            {
                return false;
            }

            allowed = true;
        }

        return allowed;
    }
}
