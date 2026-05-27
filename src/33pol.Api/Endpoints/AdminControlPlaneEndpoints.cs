using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pol33.Api;
using Pol33.Api.Contracts;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
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
        group.MapGet("/requests", ListRequests);
        group.MapPost("/models", AddModel);
        group.MapPatch("/models/{id}", UpdateModel);
        group.MapDelete("/models/{id}", RemoveModel);
        group.MapPost("/models/{id}/test", TestModel);

        return endpoints;
    }

    private static IResult GetSummary(IControlPlaneCommands commands) =>
        Results.Json(commands.GetSummary());

    private static IResult ListBackends(IControlPlaneCommands commands) =>
        Results.Json(commands.ListBackends());

    private static IResult ListModels(AdminModelProvisioningService provisioning) =>
        Results.Json(provisioning.ListModels());

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
        string id,
        CancellationToken cancellationToken)
    {
        var result = await commands.RemoveModelAsync(AdminModelRouteId.Decode(id), cancellationToken).ConfigureAwait(false);
        return Results.Json(result, statusCode: result.SuggestedStatusCode);
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
