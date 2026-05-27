using Pol33.Api.Models;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.Models;

namespace Pol33.Api.Services;

public sealed class ModelsApiService(
    IModelRegistry registry,
    IBackendHealthStore healthStore,
    IModelGrantService modelGrants)
{
    public OpenAiModelListResponse ListHealthyModels() =>
        ListHealthyModelsCore(modelId => true);

    public async Task<OpenAiModelListResponse> ListHealthyModelsAsync(
        Guid tenantId,
        Guid apiKeyId,
        CancellationToken cancellationToken = default)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in registry.GetAllModels())
        {
            if (!healthStore.IsBackendHealthy(model.Id))
            {
                continue;
            }

            if (await modelGrants.IsModelAllowedAsync(tenantId, apiKeyId, model.Id, cancellationToken)
                    .ConfigureAwait(false))
            {
                allowed.Add(model.Id);
            }
        }

        return ListHealthyModelsCore(id => allowed.Contains(id));
    }

    public (OpenAiModelResponse? Model, ErrorResult? Error) TryGetModel(string name) =>
        TryGetModelCore(name, _ => true);

    public async Task<(OpenAiModelResponse? Model, ErrorResult? Error)> TryGetModelAsync(
        string name,
        Guid tenantId,
        Guid apiKeyId,
        CancellationToken cancellationToken = default)
    {
        if (!registry.TryGetModel(name, out var model) || model is null)
        {
            return (null, ErrorResult.FromCode(
                GatewayErrorCode.ModelNotFound,
                $"Model '{name}' not found",
                "invalid_request_error"));
        }

        if (!await modelGrants.IsModelAllowedAsync(tenantId, apiKeyId, model.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            return (null, ErrorResult.FromCode(
                GatewayErrorCode.ModelNotFound,
                $"Model '{name}' not found",
                "invalid_request_error"));
        }

        return TryGetModelCore(name, id => string.Equals(id, model.Id, StringComparison.OrdinalIgnoreCase));
    }

    private OpenAiModelListResponse ListHealthyModelsCore(Func<string, bool> includeModel)
    {
        var data = registry.GetAllModels()
            .Where(model => healthStore.IsBackendHealthy(model.Id) && includeModel(model.Id))
            .Select(model => OpenAiModelMapper.ToResponse(model, available: true))
            .ToList();

        return new OpenAiModelListResponse { Data = data };
    }

    private (OpenAiModelResponse? Model, ErrorResult? Error) TryGetModelCore(
        string name,
        Func<string, bool> includeModel)
    {
        if (!registry.TryGetModel(name, out var model) || model is null)
        {
            return (null, ErrorResult.FromCode(
                GatewayErrorCode.ModelNotFound,
                $"Model '{name}' not found",
                "invalid_request_error"));
        }

        if (!includeModel(model.Id))
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
