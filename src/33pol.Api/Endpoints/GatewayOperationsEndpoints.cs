using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Api.Services;

namespace Pol33.Api.Endpoints;

public static class GatewayOperationsEndpoints
{
    public static IEndpointRouteBuilder MapGatewayOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", GetHealth);
        endpoints.MapGet("/health/ready", GetReady);
        endpoints.MapGet("/stats", GetStats);
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
