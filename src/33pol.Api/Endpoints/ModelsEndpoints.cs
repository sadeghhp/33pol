using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Api.Services;

namespace Pol33.Api.Endpoints;

public static class ModelsEndpoints
{
    public static IEndpointRouteBuilder MapModelsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/models", ListModels);
        endpoints.MapGet("/v1/models/{model}", GetModel);
        return endpoints;
    }

    private static IResult ListModels(ModelsApiService modelsApi) =>
        Results.Json(modelsApi.ListHealthyModels());

    private static IResult GetModel(string model, ModelsApiService modelsApi)
    {
        var (response, error) = modelsApi.TryGetModel(model);
        if (error is not null)
        {
            return Results.Json(error, statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(response);
    }
}
