using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Api.Services;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class GatewayOperationsEndpoints
{
    public static IEndpointRouteBuilder MapGatewayOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", GetHealth);
        endpoints.MapGet("/health/ready", GetReady);

        // Admin-gated: the snapshot carries per-model request and error counts, so serving it
        // anonymously let any caller enumerate the model inventory and read the traffic profile —
        // the same data the console gates behind an Admin key at /admin/api/summary. Probes that
        // only need up/down use /health, /health/live and /health/ready, which stay anonymous.
        endpoints.MapGet("/stats", GetStats)
            .RequireAuthorization(GatewayAuthPolicies.Admin);

        return endpoints;
    }

    private static IResult GetHealth(GatewayHealthService healthService)
    {
        var (body, statusCode) = healthService.GetHealth();
        return Results.Json(body, statusCode: statusCode);
    }

    private static IResult GetReady(GatewayReadinessService readinessService)
    {
        var (body, statusCode) = readinessService.GetReadiness();
        return Results.Json(body, statusCode: statusCode);
    }

    private static IResult GetStats(GatewayStatsService statsService) =>
        Results.Json(statsService.GetSnapshot());
}
