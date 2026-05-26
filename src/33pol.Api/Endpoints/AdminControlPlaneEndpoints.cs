using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
        group.MapGet("/requests", ListRequests);
        group.MapPost("/models", AddModel);
        group.MapPatch("/models/{id}", UpdateModel);
        group.MapDelete("/models/{id}", RemoveModel);

        return endpoints;
    }

    private static IResult GetSummary(IControlPlaneCommands commands) =>
        Results.Json(commands.GetSummary());

    private static IResult ListBackends(IControlPlaneCommands commands) =>
        Results.Json(commands.ListBackends());

    private static IResult ListModels(IControlPlaneCommands commands) =>
        Results.Json(commands.ListModels());

    private static IResult ListRequests(IControlPlaneCommands commands, int? limit) =>
        Results.Json(commands.ListRecentRequests(limit is > 0 and <= 500 ? limit.Value : 50));

    private static async Task<IResult> AddModel(
        IControlPlaneCommands commands,
        ModelConfig model,
        CancellationToken cancellationToken)
    {
        var result = await commands.AddModelAsync(model, cancellationToken).ConfigureAwait(false);
        return Results.Json(result, statusCode: result.SuggestedStatusCode);
    }

    private static async Task<IResult> UpdateModel(
        IControlPlaneCommands commands,
        string id,
        ModelConfig model,
        CancellationToken cancellationToken)
    {
        var result = await commands.UpdateModelAsync(id, model, cancellationToken).ConfigureAwait(false);
        return Results.Json(result, statusCode: result.SuggestedStatusCode);
    }

    private static async Task<IResult> RemoveModel(
        IControlPlaneCommands commands,
        string id,
        CancellationToken cancellationToken)
    {
        var result = await commands.RemoveModelAsync(id, cancellationToken).ConfigureAwait(false);
        return Results.Json(result, statusCode: result.SuggestedStatusCode);
    }
}
