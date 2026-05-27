using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class ConfigAdminEndpoints
{
    public static IEndpointRouteBuilder MapConfigAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/admin/api/config/reload", ReloadAsync)
            .RequireAuthorization(GatewayAuthPolicies.Admin);
        endpoints.MapGet("/admin/api/config/status", GetStatus)
            .RequireAuthorization(GatewayAuthPolicies.Admin);
        return endpoints;
    }

    private static async Task<IResult> ReloadAsync(
        HttpContext httpContext,
        IConfigReload configReload,
        IAuditLogger audit,
        CancellationToken cancellationToken)
    {
        var result = await configReload.ReloadAsync(cancellationToken).ConfigureAwait(false);
        audit.LogAdminAction(
            "config.reload",
            new AuditLogEntry(
                httpContext.User.FindFirst(GatewayAuthClaims.TenantId)?.Value,
                httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                new { result.Status }));

        return ToResult(result);
    }

    private static IResult GetStatus(IConfigReload configReload) =>
        Results.Json(configReload.GetStatus());

    private static IResult ToResult(ConfigReloadResult result) =>
        Results.Json(result, statusCode: result.SuggestedStatusCode);
}
