using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.Security;
using Pol33.Security.Identity;
using System.Security.Claims;

namespace Pol33.Api.Endpoints;

public static class ModelsEndpoints
{
    public static IEndpointRouteBuilder MapModelsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("")
            .RequireAuthorization(GatewayAuthPolicies.Inference);

        group.MapGet("/v1/models", ListModels);
        group.MapGet("/v1/models/{model}", GetModel);
        return endpoints;
    }

    private static async Task<IResult> ListModels(
        HttpContext httpContext,
        ModelsApiService modelsApi,
        IGatewayAuthenticationState authState,
        IErrorResponseWriter errors)
    {
        if (!TryResolveCallerIds(httpContext, out var tenantId, out var apiKeyId))
        {
            if (authState.IsAuthenticationRequired)
            {
                return Results.Json(modelsApi.ListPublicHealthyModels());
            }

            return Results.Json(modelsApi.ListHealthyModels());
        }

        var list = await modelsApi
            .ListHealthyModelsAsync(tenantId, apiKeyId, httpContext.RequestAborted)
            .ConfigureAwait(false);
        return Results.Json(list);
    }

    private static async Task<IResult> GetModel(
        string model,
        HttpContext httpContext,
        ModelsApiService modelsApi,
        IGatewayAuthenticationState authState,
        IErrorResponseWriter errors)
    {
        if (!TryResolveCallerIds(httpContext, out var tenantId, out var apiKeyId))
        {
            if (authState.IsAuthenticationRequired)
            {
                var (publicResponse, publicError) = modelsApi.TryGetPublicModel(model);
                if (publicError is not null)
                {
                    return Results.Json(publicError, statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Json(publicResponse);
            }

            var (syncResponse, syncError) = modelsApi.TryGetModel(model);
            if (syncError is not null)
            {
                return Results.Json(syncError, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(syncResponse);
        }

        var (response, error) = await modelsApi
            .TryGetModelAsync(model, tenantId, apiKeyId, httpContext.RequestAborted)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return Results.Json(error, statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(response);
    }

    private static bool TryResolveCallerIds(HttpContext context, out Guid tenantId, out Guid apiKeyId)
    {
        tenantId = default;
        apiKeyId = default;

        if (context.GetTenantContext() is TenantContext tenant &&
            Guid.TryParse(tenant.TenantId, out tenantId) &&
            Guid.TryParse(tenant.ApiKeyId, out apiKeyId))
        {
            return true;
        }

        if (context.User.Identity?.IsAuthenticated == true &&
            Guid.TryParse(context.User.FindFirstValue(GatewayAuthClaims.TenantId), out tenantId) &&
            Guid.TryParse(context.User.FindFirstValue(GatewayAuthClaims.ApiKeyId), out apiKeyId))
        {
            return true;
        }

        return false;
    }

    private static IResult Unauthorized(IErrorResponseWriter errors)
    {
        var response = errors.Write(GatewayErrorCode.InvalidApiKey);
        return Results.Content(response.Json, "application/json", statusCode: response.HttpStatusCode);
    }
}
