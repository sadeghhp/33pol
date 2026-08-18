namespace Pol33.Core.Billing;

/// <summary>
/// Which ledger rows a usage query may see.
/// </summary>
/// <param name="TenantId">
/// The caller's tenant. <see langword="null"/> means "no tenant filter" and is reserved for
/// operator-level callers (reconciliation, webhooks); admin endpoints always supply one.
/// </param>
/// <param name="IncludeAnonymous">
/// Also include rows recorded with no tenant — traffic to <c>publicAccess</c> models sent without
/// an API key. Those rows belong to nobody, so they were invisible on every tenant-scoped page even
/// though they are priced and persisted like any other request.
/// </param>
public sealed record UsageScope(Guid? TenantId, bool IncludeAnonymous = false)
{
    public static UsageScope Unrestricted { get; } = new(null, IncludeAnonymous: true);

    /// <summary>True when <paramref name="rowTenantId"/> is visible under this scope.</summary>
    public bool Matches(Guid? rowTenantId) =>
        TenantId is null
            ? IncludeAnonymous || rowTenantId is not null
            : rowTenantId == TenantId || (IncludeAnonymous && rowTenantId is null);
}
