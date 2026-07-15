using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pol33.Api.Contracts;
using Pol33.Core.Abstractions;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class AdminCorsEndpoints
{
    public static IEndpointRouteBuilder MapAdminCorsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api/cors")
            .RequireAuthorization(GatewayAuthPolicies.Admin);

        group.MapGet("/", GetAsync);
        group.MapPut("/", PutAsync);

        return endpoints;
    }

    private static Task<IResult> GetAsync(ICorsConfigAdminService service, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(Results.Json(new AdminCorsDto
        {
            AllowedOrigins = service.GetCurrent(),
        }));
    }

    private static async Task<IResult> PutAsync(
        HttpContext httpContext,
        ICorsConfigAdminService service,
        IAuditLogger audit,
        [FromBody] AdminCorsDto? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { message = "Request body is required." });
        }

        var result = await service
            .UpdateAsync(request.AllowedOrigins, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return Results.Json(new { message = result.Message }, statusCode: result.StatusCode);
        }

        audit.LogAdminAction(
            "cors.update",
            new AuditLogEntry(
                httpContext.User.FindFirst(GatewayAuthClaims.TenantId)?.Value,
                httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                new
                {
                    OriginCount = request.AllowedOrigins.Length,
                    AllowedOrigins = request.AllowedOrigins,
                }));

        return Results.Json(new { message = result.Message });
    }
}
