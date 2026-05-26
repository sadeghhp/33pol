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
        endpoints.MapGet("/stats", GetStats);
        endpoints.MapGet("/metrics", GetMetrics);
        return endpoints;
    }

    private static IResult GetHealth(GatewayHealthService healthService)
    {
        var (body, statusCode) = healthService.GetHealth();
        return Results.Json(body, statusCode: statusCode);
    }

    private static IResult GetStats(GatewayStatsService statsService) =>
        Results.Json(statsService.GetSnapshot());

    private static IResult GetMetrics() =>
        Results.Text(
            "# LLM Gateway metrics placeholder (expanded in Phase 4)\n",
            contentType: "text/plain; version=0.0.4; charset=utf-8");
}
