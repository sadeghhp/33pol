namespace Pol33.Core.Abstractions;

public interface IModelGrantService
{
    Task<bool> IsModelAllowedAsync(Guid tenantId, string canonicalModelId, CancellationToken cancellationToken = default);
}
