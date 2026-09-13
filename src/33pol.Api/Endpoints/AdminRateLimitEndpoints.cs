using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pol33.Api.Contracts;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.RateLimiting;
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
        group.MapGet("/schedule", GetSchedule);
        group.MapPost("/windows/preview", PreviewWindow);

        return endpoints;
    }

    /// <summary>
    /// The schedule report: what every rule enforces at one instant, every window occurrence in a
    /// range, and when each rule's tier changes. Read straight from the stored configuration.
    /// </summary>
    /// <remarks>
    /// <c>at</c> is the instant to evaluate at (default: now); <c>atLocal</c> plus <c>timeZone</c>
    /// is the same thing spelled as a wall-clock time in a zone, which is how an operator types
    /// it. <c>from</c>/<c>to</c> bound the calendar (default: the next seven days). <c>take</c> caps
    /// the transitions list; the response says how many there were in total.
    /// </remarks>
    private static IResult GetSchedule(
        IRateLimitConfigAdminService service,
        TimeProvider? timeProvider,
        [FromQuery] DateTimeOffset? at,
        [FromQuery] string? atLocal,
        [FromQuery] string? timeZone,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? take)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        var reference = at ?? now;
        if (!string.IsNullOrWhiteSpace(atLocal))
        {
            if (!TryResolveLocal(atLocal, timeZone, out reference, out var error))
            {
                return Results.BadRequest(new { message = error });
            }
        }

        var rangeFrom = from ?? now;
        var rangeTo = to ?? rangeFrom.AddDays(7);
        if (rangeTo <= rangeFrom)
        {
            return Results.BadRequest(new { message = "to must be after from." });
        }

        if (rangeTo - rangeFrom > TimeSpan.FromDays(62))
        {
            return Results.BadRequest(new { message = "The calendar range may not exceed 62 days." });
        }

        return Results.Json(service.GetSchedule(reference, rangeFrom, rangeTo, take ?? 50));
    }

    /// <summary>A wall-clock time in a zone (<c>yyyy-MM-ddTHH:mm</c>) as an instant.</summary>
    private static bool TryResolveLocal(string local, string? timeZone, out DateTimeOffset instant, out string? error)
    {
        instant = default;
        error = null;

        if (!DateTime.TryParse(
                local,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            error = "atLocal must be a date and time, yyyy-MM-ddTHH:mm.";
            return false;
        }

        if (!RateLimitScheduleEvaluator.TryResolveTimeZone(timeZone, out var zone))
        {
            error = $"time zone '{timeZone}' is not known on this host.";
            return false;
        }

        var unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        instant = new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
        return true;
    }

    /// <summary>
    /// What a window an operator is composing would do, before it is saved. Pure computation over
    /// the submitted rule; nothing is persisted.
    /// </summary>
    private static IResult PreviewWindow(
        IRateLimitConfigAdminService service,
        [FromBody] AdminRateLimitWindowPreviewDto? request)
    {
        if (request is null)
        {
            return Results.BadRequest(new { message = "Request body is required." });
        }

        var rule = new RateLimitRuleDefinition(
            request.Scope?.Trim() ?? string.Empty,
            request.Target ?? string.Empty,
            request.Rpm,
            request.Burst,
            request.MaxConcurrentStreams)
        {
            Schedule = request.Windows.Where(static w => w is not null).Select(static w => w.ToDefinition()).ToArray(),
        };

        return Results.Json(service.PreviewWindow(rule, request.Candidate?.Trim() ?? string.Empty));
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
                    WindowCount = rules?.Sum(static r => r.Windows.Count),
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
