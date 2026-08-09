namespace Pol33.Security.Configuration;

/// <summary>
/// Names the tenant whose admin keys operate the gateway itself — model registry and upstream
/// credentials, providers, CORS, rate limits, config reload, backups, and the cross-tenant
/// request/log feeds.
/// </summary>
/// <remarks>
/// The Admin <em>role</em> is per-tenant: any tenant's admin manages its own keys and grants, and
/// can mint further admin keys for its own tenant. The global control plane therefore cannot be
/// gated on the role alone — that handed every tenant's admin the whole gateway. Resolution order:
/// <c>Gateway:Security:OperatorTenantSlug</c> when set, else the bootstrap tenant slug
/// (<c>Gateway:Bootstrap:TenantSlug</c>, default <c>default</c>) — so a single-tenant deployment
/// that never touched either setting keeps working unchanged, since its only tenant is the
/// bootstrap tenant.
/// </remarks>
public sealed record OperatorTenantConfiguration(string TenantSlug)
{
    public const string FallbackTenantSlug = "default";

    public bool IsOperatorTenant(string? tenantSlug) =>
        !string.IsNullOrWhiteSpace(tenantSlug) &&
        string.Equals(tenantSlug.Trim(), TenantSlug, StringComparison.OrdinalIgnoreCase);
}
