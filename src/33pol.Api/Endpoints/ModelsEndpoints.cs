using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;

namespace Pol33.Api.Endpoints;

public static class ModelsEndpoints
{
    public static IEndpointRouteBuilder MapModelsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/models", ListModels);
        endpoints.MapGet("/v1/models/{model}", GetModel);
        return endpoints;
    }

    private static async Task<IResult> ListModels(
        HttpContext httpContext,
        ModelsApiService modelsApi,
        IGatewayAuthenticationState authState)
    {
        if (authState.IsAuthenticationRequired && TryGetCaller(httpContext, out var tenantId, out var apiKeyId))
        {
            var list = await modelsApi.ListHealthyModelsAsync(tenantId, apiKeyId, httpContext.RequestAborted)
                .ConfigureAwait(false);
            return Results.Json(list);
        }

        return Results.Json(modelsApi.ListHealthyModels());
    }

    private static async Task<IResult> GetModel(
        string model,
        HttpContext httpContext,
        ModelsApiService modelsApi,
        IGatewayAuthenticationState authState)
    {
        if (authState.IsAuthenticationRequired && TryGetCaller(httpContext, out var tenantId, out var apiKeyId))
        {
            var (response, error) = await modelsApi
                .TryGetModelAsync(model, tenantId, apiKeyId, httpContext.RequestAborted)
                .ConfigureAwait(false);
            if (error is not null)
            {
                return Results.Json(error, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(response);
        }

        var (syncResponse, syncError) = modelsApi.TryGetModel(model);
        if (syncError is not null)
        {
            return Results.Json(syncError, statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(syncResponse);
    }

    private static bool TryGetCaller(HttpContext context, out Guid tenantId, out Guid apiKeyId)
    {
        tenantId = default;
        apiKeyId = default;
        if (!context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var value) ||
            value is not TenantContext tenant)
        {
            return false;
        }

        return Guid.TryParse(tenant.TenantId, out tenantId)
            && Guid.TryParse(tenant.ApiKeyId, out apiKeyId);
    }
}
