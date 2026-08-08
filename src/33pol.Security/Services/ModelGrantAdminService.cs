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
        return ToTenantResponse(grants.Select(g => g.ModelPattern).ToList());
    }

    public async Task<ModelGrantsResponse> ReplaceTenantGrantsAsync(
        Guid tenantId,
        ReplaceModelGrantsRequest request,
        CancellationToken cancellationToken = default)
    {
        var modelIds = ValidateModelIds(request.ModelIds);

        // An empty tenant allowlist removes the ceiling entirely rather than removing access, so it
        // must be asked for explicitly. Submitting an empty list reads as "revoke everything" and
        // silently did the opposite: it promoted the tenant from its granted models to every model
        // in the registry.
        if (modelIds.Count == 0 && !request.AllowAllModels)
        {
            throw new ArgumentException(
                "An empty tenant model list removes the tenant ceiling, allowing every model in the "
                + "registry — it does not revoke access. Set allowAllModels=true to confirm that is "
                + "intended. To restrict the tenant instead, submit the models it may use; to remove "
                + "all access, clear the grants on its API keys.",
                nameof(request));
        }

        await _tenantGrants.ReplaceForTenantAsync(tenantId, modelIds, cancellationToken).ConfigureAwait(false);
        _grantService.InvalidateTenantGrants(tenantId);
        return ToTenantResponse(modelIds);
    }

    public async Task<ModelGrantsResponse> GetApiKeyGrantsAsync(
        Guid tenantId,
        Guid apiKeyId,
        CancellationToken cancellationToken = default)
    {
        var key = await RequireInferenceKeyAsync(tenantId, apiKeyId, cancellationToken).ConfigureAwait(false);
        _ = key;
        var grants = await _apiKeyGrants.ListByApiKeyAsync(apiKeyId, cancellationToken).ConfigureAwait(false);
        return ToApiKeyResponse(grants.Select(g => g.ModelPattern).ToList());
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
        return ToApiKeyResponse(modelIds);
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
        var canonicalIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawId in modelIds)
        {
            if (string.IsNullOrWhiteSpace(rawId))
            {
                continue;
            }

            var trimmed = rawId.Trim();
            if (!_registry.TryGetModel(trimmed, out var model) || model is null)
            {
                throw new ArgumentException($"Model '{trimmed}' is not registered.", nameof(modelIds));
            }

            if (seen.Add(model.Id))
            {
                canonicalIds.Add(model.Id);
            }
        }

        return canonicalIds;
    }

    private static ModelGrantsResponse ToTenantResponse(IReadOnlyList<string> modelIds) =>
        new()
        {
            ModelIds = modelIds,
            UsesDefaultAccess = modelIds.Count == 0,
        };

    private static ModelGrantsResponse ToApiKeyResponse(IReadOnlyList<string> modelIds) =>
        new()
        {
            ModelIds = modelIds,
            UsesDefaultAccess = false,
        };
}
