using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Pol33.Api;
using Pol33.Api.Contracts;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class AdminControlPlaneEndpoints
{
    public static IEndpointRouteBuilder MapAdminControlPlaneEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api")
            .RequireAuthorization(GatewayAuthPolicies.Operator);

        group.MapGet("/summary", GetSummary);
        group.MapGet("/backends", ListBackends);
        group.MapGet("/models", ListModels);
        group.MapGet("/model-types", GetModelTypes);
        group.MapGet("/requests", ListRequests);
        group.MapGet("/live", StreamLive);
        group.MapPost("/models", AddModel);
        group.MapPatch("/models/{id}", UpdateModel);
        group.MapDelete("/models/{id}", RemoveModel);
        group.MapPost("/models/{id}/test", TestModel);
        group.MapGet("/logs", ListLogs);
        group.MapDelete("/logs", ClearLogs);

        return endpoints;
    }

    private static IResult GetSummary(IControlPlaneCommands commands) =>
        Results.Json(commands.GetSummary());

    private static IResult ListBackends(IControlPlaneCommands commands) =>
        Results.Json(commands.ListBackends());

    private static async Task<IResult> ListModels(
        AdminModelProvisioningService provisioning,
        CancellationToken cancellationToken) =>
        Results.Json(await provisioning.ListModelsAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The canonical model-type taxonomy, so the admin UI does not keep its own copy. The UI's
    /// hand-maintained duplicate had drifted to a fraction of the accepted aliases, which made
    /// correctly-typed models display as text generation and silently be rewritten on save.
    /// </summary>
    private static IResult GetModelTypes() =>
        Results.Json(AdminModelTypeDescriptor.All());

    private static IResult ListRequests(IControlPlaneCommands commands, int? limit) =>
        Results.Json(commands.ListRecentRequests(limit is > 0 and <= 500 ? limit.Value : 50));

    private static readonly JsonSerializerOptions LiveJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The Overview's push channel: a server-sent-event stream that emits a
    /// <c>{ version, summary, requests }</c> frame whenever the gateway's activity changes, so a
    /// request appears the moment it is admitted and its cost the moment it is priced — not on the
    /// next 2-second poll. Frames are coalesced to at most one every <see cref="LiveMinInterval"/>;
    /// a comment heartbeat keeps intermediaries from closing an idle connection.
    /// </summary>
    /// <remarks>
    /// Polling is not removed: the console falls back to it when the stream cannot be established
    /// (a proxy that buffers, a browser that lost the connection), so this endpoint is an upgrade,
    /// not a dependency.
    /// </remarks>
    private static async Task StreamLive(
        HttpContext httpContext,
        IControlPlaneCommands commands,
        IAdminLiveFeed feed,
        ILoggerFactory loggerFactory,
        int? limit,
        CancellationToken cancellationToken)
    {
        var take = limit is > 0 and <= 500 ? limit.Value : 50;
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers.Connection = "keep-alive";
        // Tells nginx-style proxies not to buffer the stream.
        response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        long? sent = null;
        var lastWrite = DateTimeOffset.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var version = feed.Version;
                var now = DateTimeOffset.UtcNow;

                if (sent != version)
                {
                    var frame = new
                    {
                        version,
                        summary = commands.GetSummary(),
                        requests = commands.ListRecentRequests(take),
                    };

                    await response.WriteAsync("event: update\ndata: ", cancellationToken).ConfigureAwait(false);
                    await response.WriteAsync(JsonSerializer.Serialize(frame, LiveJson), cancellationToken).ConfigureAwait(false);
                    await response.WriteAsync("\n\n", cancellationToken).ConfigureAwait(false);
                    await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    sent = version;
                    lastWrite = now;
                }
                else if (now - lastWrite >= LiveHeartbeat)
                {
                    await response.WriteAsync(": ping\n\n", cancellationToken).ConfigureAwait(false);
                    await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    lastWrite = now;
                }

                await Task.Delay(LiveMinInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The browser navigated away or the tab closed; nothing to report.
        }
        catch (Exception ex)
        {
            // The response is long since committed, so this cannot become an error page. Letting it
            // escape would reach the terminal handler, be recorded as a gateway error, and — because
            // the console reconnects — repeat for as long as the fault persists, burying real faults
            // in the Errors tab. Close the stream instead and let the client fall back to polling.
            loggerFactory
                .CreateLogger(typeof(AdminControlPlaneEndpoints).FullName!)
                .LogWarning(ex, "Admin live stream ended early; the console will fall back to polling.");
        }
    }

    private static readonly TimeSpan LiveMinInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LiveHeartbeat = TimeSpan.FromSeconds(15);

    private static async Task<IResult> AddModel(
        AdminModelProvisioningService provisioning,
        [FromBody] AdminModelWriteRequest? request,
        CancellationToken cancellationToken)
    {
        if (request?.Model is null)
        {
            return Results.BadRequest(new { message = "Request body must include model." });
        }

        var result = await provisioning.AddAsync(request, cancellationToken).ConfigureAwait(false);
        return Results.Json(result, statusCode: result.SuggestedStatusCode);
    }

    private static async Task<IResult> UpdateModel(
        AdminModelProvisioningService provisioning,
        string id,
        [FromBody] AdminModelWriteRequest? request,
        CancellationToken cancellationToken)
    {
        if (request?.Model is null)
        {
            return Results.BadRequest(new { message = "Request body must include model." });
        }

        var result = await provisioning.UpdateAsync(AdminModelRouteId.Decode(id), request, cancellationToken).ConfigureAwait(false);
        return Results.Json(result, statusCode: result.SuggestedStatusCode);
    }

    private static async Task<IResult> RemoveModel(
        IControlPlaneCommands commands,
        IRateCardAdminService pricing,
        string id,
        CancellationToken cancellationToken)
    {
        var modelId = AdminModelRouteId.Decode(id);
        var result = await commands.RemoveModelAsync(modelId, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            // Drop pricing too, so a model later re-created under the same id does not
            // silently inherit a stale rate card.
            await pricing.ClearPricingAsync(modelId, cancellationToken).ConfigureAwait(false);
        }

        return Results.Json(result, statusCode: result.SuggestedStatusCode);
    }

    /// <summary>
    /// Recent gateway diagnostics, newest first. <paramref name="level"/> is a floor
    /// (<c>warning</c> hides info), <paramref name="search"/> a case-insensitive substring.
    /// </summary>
    private static IResult ListLogs(
        IGatewayLogStore logs,
        int? limit,
        string? level,
        string? search)
    {
        var take = limit is > 0 and <= 500 ? limit.Value : 100;

        // Fetch the whole matching set and slice, so the response can report how much the limit hid.
        // The buffer is bounded at a few hundred entries, so this is one cheap pass.
        var matched = logs.GetRecent(logs.Capacity, GatewayLogLevels.ParseFilter(level), search);
        var entries = matched.Count <= take ? matched : matched.Take(take).ToList();

        return Results.Json(new AdminLogListResponse
        {
            Entries = [.. entries.Select(AdminLogEntryDto.From)],
            Count = entries.Count,
            Total = matched.Count,
            Capacity = logs.Capacity,
        });
    }

    private static IResult ClearLogs(HttpContext httpContext, IGatewayLogStore logs, IAuditLogger audit)
    {
        var cleared = logs.Clear();

        // Wiping the diagnostic buffer is a mutation an operator can perform mid-incident; it needs
        // to leave a trace like every other admin mutation does.
        audit.LogAdminAction(
            "logs.clear",
            new AuditLogEntry(
                httpContext.User.FindFirst(GatewayAuthClaims.TenantId)?.Value,
                httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                new { entriesCleared = cleared }));

        return Results.Json(new { success = true, message = "Log buffer cleared.", entriesCleared = cleared });
    }

    private static async Task<IResult> TestModel(
        AdminModelTestService testService,
        string id,
        [FromBody] AdminModelTestRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await testService.TestAsync(AdminModelRouteId.Decode(id), request, cancellationToken).ConfigureAwait(false);
        return Results.Json(result, statusCode: result.SuggestedStatusCode);
    }
}
