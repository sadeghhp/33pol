namespace Pol33.Core.Models;

public sealed class ModelGrantsResponse
{
    public required IReadOnlyList<string> ModelIds { get; init; }

    /// <summary>
    /// Tenant: true when no explicit tenant allowlist (all registry models allowed at tenant level).
    /// API key: always false — keys require an explicit allowlist; an empty list means no model access.
    /// </summary>
    public bool UsesDefaultAccess { get; init; }
}

public sealed class ReplaceModelGrantsRequest
{
    public IReadOnlyList<string> ModelIds { get; init; } = [];
}
