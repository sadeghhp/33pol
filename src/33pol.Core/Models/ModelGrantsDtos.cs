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

    /// <summary>
    /// Required to submit an empty <see cref="ModelIds"/> for a <em>tenant</em>, because an empty
    /// tenant allowlist means "no ceiling — every model in the registry", not "no access".
    /// </summary>
    /// <remarks>
    /// This exists because the two readings are opposites and the dangerous one was the default.
    /// Clearing the list reads as revoking access, but it actually widens a tenant from its handful
    /// of granted models to everything the gateway routes. Requiring the caller to say so turns a
    /// silent privilege escalation into a deliberate one. Ignored for API-key grants, where an empty
    /// list unambiguously means no access.
    /// </remarks>
    public bool AllowAllModels { get; init; }
}
