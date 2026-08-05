using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
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
            .RequireAuthorization(GatewayAuthPolicies.Admin);

        group.MapGet("/summary", GetSummary);
        group.MapGet("/backends", ListBackends);
        group.MapGet("/models", ListModels);
        group.MapGet("/model-types", GetModelTypes);
        group.MapGet("/requests", ListRequests);
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
        var entries = logs.GetRecent(take, GatewayLogLevels.ParseFilter(level), search);

        return Results.Json(new AdminLogListResponse
        {
            Entries = [.. entries.Select(AdminLogEntryDto.From)],
            Count = entries.Count,
            Capacity = logs.Capacity,
        });
    }

    private static IResult ClearLogs(IGatewayLogStore logs)
    {
        logs.Clear();
        return Results.Json(new { success = true, message = "Log buffer cleared." });
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
