using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Models;

namespace Pol33.Security.Services;

public sealed class ModelGrantAdminService : IModelGrantAdminService
{
    private readonly IModelGrantRepository _tenantGrants;
    private readonly IApiKeyModelGrantRepository _apiKeyGrants;
    private readonly IApiKeyRepository _apiKeys;
    private readonly IModelRegistry _registry;
    private readonly IModelGrantService _grantService;

    public ModelGrantAdminService(
        IModelGrantRepository tenantGrants,
        IApiKeyModelGrantRepository apiKeyGrants,
        IApiKeyRepository apiKeys,
        IModelRegistry registry,
        IModelGrantService grantService)
    {
        _tenantGrants = tenantGrants;
        _apiKeyGrants = apiKeyGrants;
        _apiKeys = apiKeys;
        _registry = registry;
        _grantService = grantService;
    }

    public async Task<ModelGrantsResponse> GetTenantGrantsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var grants = await _tenantGrants.ListByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return ToResponse(grants.Select(g => g.ModelPattern).ToList(), grants.Count == 0);
    }

    public async Task<ModelGrantsResponse> ReplaceTenantGrantsAsync(
        Guid tenantId,
        ReplaceModelGrantsRequest request,
        CancellationToken cancellationToken = default)
    {
        var modelIds = ValidateModelIds(request.ModelIds);
        await _tenantGrants.ReplaceForTenantAsync(tenantId, modelIds, cancellationToken).ConfigureAwait(false);
        _grantService.InvalidateTenantGrants(tenantId);
        return ToResponse(modelIds, modelIds.Count == 0);
    }

    public async Task<ModelGrantsResponse> GetApiKeyGrantsAsync(
        Guid tenantId,
        Guid apiKeyId,
        CancellationToken cancellationToken = default)
    {
        var key = await RequireInferenceKeyAsync(tenantId, apiKeyId, cancellationToken).ConfigureAwait(false);
        _ = key;
        var grants = await _apiKeyGrants.ListByApiKeyAsync(apiKeyId, cancellationToken).ConfigureAwait(false);
        return ToResponse(grants.Select(g => g.ModelPattern).ToList(), grants.Count == 0);
    }

    public async Task<ModelGrantsResponse> ReplaceApiKeyGrantsAsync(
        Guid tenantId,
        Guid apiKeyId,
        ReplaceModelGrantsRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireInferenceKeyAsync(tenantId, apiKeyId, cancellationToken).ConfigureAwait(false);
        var modelIds = ValidateModelIds(request.ModelIds);
        await EnsureKeyGrantsWithinTenantAsync(tenantId, modelIds, cancellationToken).ConfigureAwait(false);
        await _apiKeyGrants.ReplaceForApiKeyAsync(apiKeyId, modelIds, cancellationToken).ConfigureAwait(false);
        _grantService.InvalidateApiKeyGrants(apiKeyId);
        return ToResponse(modelIds, modelIds.Count == 0);
    }

    private async Task<ApiKeyRecord> RequireInferenceKeyAsync(
        Guid tenantId,
        Guid apiKeyId,
        CancellationToken cancellationToken)
    {
        var key = await _apiKeys.GetByIdAsync(apiKeyId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"API key '{apiKeyId}' was not found.");

        if (key.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("API key does not belong to the current tenant.");
        }

        if (key.Role is not ApiKeyRole.Inference and not ApiKeyRole.Both)
        {
            throw new InvalidOperationException("Model grants apply only to inference API keys.");
        }

        return key;
    }

    private async Task EnsureKeyGrantsWithinTenantAsync(
        Guid tenantId,
        IReadOnlyList<string> modelIds,
        CancellationToken cancellationToken)
    {
        if (modelIds.Count == 0)
        {
            return;
        }

        var tenantList = await _tenantGrants.ListByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        foreach (var id in modelIds)
        {
            if (!ModelGrantEvaluator.IsModelAllowed(tenantList, id))
            {
                throw new ArgumentException(
                    $"Model '{id}' is not allowed by the tenant model policy.",
                    nameof(modelIds));
            }
        }
    }

    private IReadOnlyList<string> ValidateModelIds(IReadOnlyList<string> modelIds)
    {
        var normalized = modelIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var id in normalized)
        {
            if (!_registry.TryGetModel(id, out var model) || model is null)
            {
                throw new ArgumentException($"Model '{id}' is not registered.", nameof(modelIds));
            }
        }

        return normalized;
    }

    private static ModelGrantsResponse ToResponse(IReadOnlyList<string> modelIds, bool usesDefaultAccess) =>
        new()
        {
            ModelIds = modelIds,
            UsesDefaultAccess = usesDefaultAccess,
        };
}
