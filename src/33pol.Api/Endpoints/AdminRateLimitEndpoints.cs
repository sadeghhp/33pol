using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pol33.Api.Contracts;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class AdminRateLimitEndpoints
{
    public static IEndpointRouteBuilder MapAdminRateLimitEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api/rate-limits")
            .RequireAuthorization(GatewayAuthPolicies.Admin);

        group.MapGet("/", GetAsync);
        group.MapPut("/", PutAsync);

        return endpoints;
    }

    private static Task<IResult> GetAsync(IRateLimitConfigAdminService service, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var current = service.GetCurrent();
        return Task.FromResult(Results.Json(ToDto(current)));
    }

    private static async Task<IResult> PutAsync(
        HttpContext httpContext,
        IRateLimitConfigAdminService service,
        IAuditLogger audit,
        [FromBody] AdminRateLimitsDto? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { message = "Request body is required." });
        }

        var result = await service
            .UpdateAsync(request.Enabled, request.Default, request.Plans, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return Results.Json(new { message = result.Message }, statusCode: result.StatusCode);
        }

        audit.LogAdminAction(
            "rate_limits.update",
            new AuditLogEntry(
                httpContext.User.FindFirst(GatewayAuthClaims.TenantId)?.Value,
                httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                new
                {
                    request.Enabled,
                    request.Default.Rpm,
                    request.Default.Burst,
                    request.Default.MaxConcurrentStreams,
                    PlanCount = request.Plans.Count,
                }));

        return Results.Json(new { message = result.Message });
    }

    private static AdminRateLimitsDto ToDto(Core.Configuration.RateLimitAdminConfig config) =>
        new()
        {
            Enabled = config.Enabled,
            Default = new Core.Configuration.RateLimitTierOptions
            {
                Rpm = config.Default.Rpm,
                Burst = config.Default.Burst,
                MaxConcurrentStreams = config.Default.MaxConcurrentStreams,
            },
            Plans = config.Plans.ToDictionary(
                static p => p.Key,
                static p => new Core.Configuration.RateLimitTierOptions
                {
                    Rpm = p.Value.Rpm,
                    Burst = p.Value.Burst,
                    MaxConcurrentStreams = p.Value.MaxConcurrentStreams,
                },
                StringComparer.OrdinalIgnoreCase),
        };
}
