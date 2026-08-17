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
    /// Listing for callers with no usable credential on a gateway that requires one. Every healthy
    /// model is shown so the caller can discover what the gateway offers, and each is tagged with
    /// <c>requires_api_key</c> so it is obvious which ones need a key. Public models are marked
    /// <c>false</c>; everything else <c>true</c>. A <c>help</c> line explains how to authenticate.
    /// </summary>
    public OpenAiModelListResponse ListAnonymousHealthyModels()
    {
        var data = registry.GetAllModels()
            .Where(model => healthStore.IsBackendHealthy(model.Id))
            .Select(model => OpenAiModelMapper.ToResponse(
                model,
                available: true,
                requiresApiKey: !model.AllowsPublicGatewayAccess()))
            .ToList();

        return new OpenAiModelListResponse
        {
            Data = data,
            Help = data.Any(m => m.RequiresApiKey == true) ? AnonymousHelpText : null,
        };
    }

    public const string AnonymousHelpText =
        "Models with \"requires_api_key\": true need an inference API key. " +
        "Ask the gateway administrator for a key and send it as the header " +
        "'Authorization: Bearer <your-key>'. Models with \"requires_api_key\": false can be used without a key.";

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

    /// <summary>
    /// Single-model lookup for anonymous callers on a gateway that requires a key. Mirrors
    /// <see cref="ListAnonymousHealthyModels"/>: the model is returned (never hidden as 404) with
    /// <c>requires_api_key</c> set so the caller learns whether a key is needed.
    /// </summary>
    public (OpenAiModelResponse? Model, ErrorResult? Error) TryGetAnonymousModel(string name)
    {
        if (!registry.TryGetModel(name, out var model) || model is null)
        {
            return (null, ErrorResult.FromCode(
                GatewayErrorCode.ModelNotFound,
                $"Model '{name}' not found",
                "invalid_request_error"));
        }

        var available = healthStore.IsBackendHealthy(model.Id);
        return (OpenAiModelMapper.ToResponse(model, available, requiresApiKey: !model.AllowsPublicGatewayAccess()), null);
    }

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
            .Where(model => healthStore.IsBackendHealthy(model.Id) && includeModel(model))
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
