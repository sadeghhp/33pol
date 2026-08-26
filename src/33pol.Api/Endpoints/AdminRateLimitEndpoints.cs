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
            .RequireAuthorization(GatewayAuthPolicies.Operator);

        group.MapGet("/", GetAsync);
        group.MapPut("/", PutAsync);
        group.MapGet("/usage", GetUsageAsync);

        return endpoints;
    }

    /// <summary>
    /// The usage report: who is sending what, against which limits, and where those limits are being
    /// hit.
    /// </summary>
    /// <remarks>
    /// A read of in-memory counters, so it is cheap enough to poll and safe to call during an
    /// incident — it takes no database connection and touches nothing on the request path. The
    /// window is capped at what the counters actually hold; asking for more returns the longest
    /// available rather than an error, and the response says which window it answered.
    /// </remarks>
    private static IResult GetUsageAsync(
        IRateLimitUsageTracker? tracker,
        TimeProvider? timeProvider,
        [FromQuery] int? minutes,
        [FromQuery] int? take)
    {
        if (tracker is null)
        {
            return Results.Json(
                new { message = "Rate-limit usage tracking is not enabled in this deployment." },
                statusCode: 503);
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return Results.Json(tracker.BuildReport(minutes ?? 60, take ?? 25, now));
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

        // Null rules pass through as null so the service leaves the stored set alone; an empty list
        // is a deliberate "delete them all" and is passed through as such.
        var rules = request.Rules?.Select(static r => r.ToDefinition()).ToArray();

        var result = await service
            .UpdateAsync(
                request.Enabled,
                request.AdaptiveEnabled,
                request.Default,
                request.Plans,
                rules,
                cancellationToken)
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
                    request.AdaptiveEnabled,
                    request.Default.Rpm,
                    request.Default.Burst,
                    request.Default.MaxConcurrentStreams,
                    PlanCount = request.Plans.Count,
                    RuleCount = rules?.Length,
                }));

        return Results.Json(new { message = result.Message });
    }

    private static AdminRateLimitsDto ToDto(Core.Configuration.RateLimitAdminConfig config) =>
        new()
        {
            Enabled = config.Enabled,
            AdaptiveEnabled = config.AdaptiveEnabled,
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
            Rules = [.. config.Rules.Select(AdminRateLimitRuleDto.FromDefinition)],
        };
}
