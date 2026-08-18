using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Security;

namespace Pol33.Api;

/// <summary>
/// The tenant and API key an admin mutation is performed under, captured at the endpoint so
/// services that have no <see cref="HttpContext"/> can still stamp the actor on their audit entries.
/// </summary>
/// <remarks>
/// The provisioning service used to write its secret-store and pricing audits with a null actor
/// because it is a singleton with no request context. That left changes to a model's upstream URL
/// or credential — the mutations that redirect tenant traffic — untraceable to an operator key.
/// </remarks>
public sealed record AdminActor(string? TenantId, string? ApiKeyId)
{
    /// <summary>An actor with no identity, for callers outside a request (tests, tooling).</summary>
    public static AdminActor Anonymous { get; } = new(null, null);

    public static AdminActor FromHttpContext(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return new AdminActor(
            httpContext.User.FindFirst(GatewayAuthClaims.TenantId)?.Value,
            httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value);
    }

    public AuditLogEntry ToAuditEntry(object? details = null) => new(TenantId, ApiKeyId, details);
}
