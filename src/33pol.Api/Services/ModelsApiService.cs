using Pol33.Api.Models;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Models;

namespace Pol33.Api.Services;

public sealed class ModelsApiService(IModelRegistry registry, IBackendHealthStore healthStore)
{
    public OpenAiModelListResponse ListHealthyModels()
    {
        var data = registry.GetAllModels()
            .Where(model => healthStore.IsBackendHealthy(model.Id))
            .Select(model => OpenAiModelMapper.ToResponse(model, available: true))
            .ToList();

        return new OpenAiModelListResponse { Data = data };
    }

    public (OpenAiModelResponse? Model, ErrorResult? Error) TryGetModel(string name)
    {
        if (!registry.TryGetModel(name, out var model) || model is null)
        {
            return (null, ErrorResult.FromCode(
                GatewayErrorCode.ModelNotFound,
                $"Model '{name}' not found",
                "invalid_request_error"));
        }

        var available = healthStore.IsBackendHealthy(model.Id);
        return (OpenAiModelMapper.ToResponse(model, available), null);
    }
}
