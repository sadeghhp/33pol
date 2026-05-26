using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Api.Endpoints;

/// <summary>
/// Phase 2: admin config routes are unauthenticated (security debt — secured in Phase 3).
/// </summary>
public static class ConfigAdminEndpoints
{
    public static IEndpointRouteBuilder MapConfigAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/admin/api/config/reload", ReloadAsync);
        endpoints.MapGet("/admin/api/config/status", GetStatus);
        return endpoints;
    }

    private static async Task<IResult> ReloadAsync(
        IConfigReload configReload,
        CancellationToken cancellationToken)
    {
        var result = await configReload.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(result);
    }

    private static IResult GetStatus(IConfigReload configReload)
    {
        return Results.Json(configReload.GetStatus());
    }

    private static IResult ToResult(ConfigReloadResult result) =>
        Results.Json(result, statusCode: result.SuggestedStatusCode);
}
