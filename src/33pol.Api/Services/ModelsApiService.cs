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
        ListHealthyModelsCore(_ => true);

    /// <summary>
    /// Listing for callers with no usable credential on a gateway that requires one.
    /// <c>data</c> keeps the OpenAI shape and holds only the models the caller can actually use
    /// (public ones). <c>models</c> is a minimal inventory of every healthy model with an
    /// <c>api_key_required</c> flag, so the caller can see what exists and that a key is needed.
    /// </summary>
    public OpenAiModelListResponse ListAnonymousHealthyModels()
    {
        var healthy = registry.GetAllModels()
            .Where(model => model.IsServing() && healthStore.IsBackendHealthy(model.Id))
            .ToList();

        return new OpenAiModelListResponse
        {
            Data = healthy
                .Where(model => model.AllowsPublicGatewayAccess())
                .Select(model => OpenAiModelMapper.ToResponse(model, available: true))
                .ToList(),
            Models = healthy
                .Select(model => new ModelAvailabilityHint
                {
                    Id = model.Id,
                    ApiKeyRequired = !model.AllowsPublicGatewayAccess(),
                })
                .ToList(),
        };
    }

    public async Task<OpenAiModelListResponse> ListHealthyModelsAsync(
        Guid tenantId,
        Guid apiKeyId,
        CancellationToken cancellationToken = default)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in registry.GetAllModels())
        {
            if (!model.IsServing() || !healthStore.IsBackendHealthy(model.Id))
            {
                continue;
            }

            if (model.AllowsPublicGatewayAccess())
            {
                allowed.Add(model.Id);
                continue;
            }

            if (await modelGrants.IsModelAllowedAsync(tenantId, apiKeyId, model.Id, cancellationToken)
                    .ConfigureAwait(false))
            {
                allowed.Add(model.Id);
            }
        }

        return ListHealthyModelsCore(model => allowed.Contains(model.Id));
    }

    public (OpenAiModelResponse? Model, ErrorResult? Error) TryGetModel(string name) =>
        TryGetModelCore(name, _ => true);

    public (OpenAiModelResponse? Model, ErrorResult? Error) TryGetPublicModel(string name)
    {
        if (!registry.TryGetModel(name, out var model) || model is null || model.IsStopped() || !model.AllowsPublicGatewayAccess())
        {
            return (null, ErrorResult.FromCode(
                GatewayErrorCode.ModelNotFound,
                $"Model '{name}' not found",
                "invalid_request_error"));
        }

        return TryGetModelCore(name, id => string.Equals(id, model.Id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(OpenAiModelResponse? Model, ErrorResult? Error)> TryGetModelAsync(
        string name,
        Guid tenantId,
        Guid apiKeyId,
        CancellationToken cancellationToken = default)
    {
        if (!registry.TryGetModel(name, out var model) || model is null || model.IsStopped())
        {
            return (null, ErrorResult.FromCode(
                GatewayErrorCode.ModelNotFound,
                $"Model '{name}' not found",
                "invalid_request_error"));
        }

        if (!model.AllowsPublicGatewayAccess() &&
            !await modelGrants.IsModelAllowedAsync(tenantId, apiKeyId, model.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            return (null, ErrorResult.FromCode(
                GatewayErrorCode.ModelNotFound,
                $"Model '{name}' not found",
                "invalid_request_error"));
        }

        return TryGetModelCore(name, id => string.Equals(id, model.Id, StringComparison.OrdinalIgnoreCase));
    }

    private OpenAiModelListResponse ListHealthyModelsCore(Func<ModelConfig, bool> includeModel)
    {
        var data = registry.GetAllModels()
            .Where(model => model.IsServing() && healthStore.IsBackendHealthy(model.Id) && includeModel(model))
            .Select(model => OpenAiModelMapper.ToResponse(model, available: true))
            .ToList();

        return new OpenAiModelListResponse { Data = data };
    }

    private (OpenAiModelResponse? Model, ErrorResult? Error) TryGetModelCore(
        string name,
        Func<string, bool> includeModel)
    {
        // A stopped route is invisible here for the same reason it is absent from the listing: the
        // gateway will not forward to it, so reporting it as an available model would hand callers a
        // model id that answers 404 on first use.
        if (!registry.TryGetModel(name, out var model) || model is null || model.IsStopped())
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
