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

        return grants.Any(g =>
            g.Effect == GrantEffect.Allow
            && string.Equals(g.ModelPattern, canonicalModelId, StringComparison.OrdinalIgnoreCase));
    }
}
