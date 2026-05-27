namespace Pol33.Core.Abstractions;

public interface IModelGrantService
{
    Task<bool> IsModelAllowedAsync(
        Guid tenantId,
        Guid apiKeyId,
        string canonicalModelId,
        CancellationToken cancellationToken = default);

    void InvalidateTenantGrants(Guid tenantId);

    void InvalidateApiKeyGrants(Guid apiKeyId);
}
