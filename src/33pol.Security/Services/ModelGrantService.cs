using Pol33.Core.Abstractions;
using Pol33.Core.Identity;

namespace Pol33.Security.Services;

public sealed class ModelGrantService : IModelGrantService
{
    private readonly IModelGrantRepository _grants;

    public ModelGrantService(IModelGrantRepository grants) => _grants = grants;

    public async Task<bool> IsModelAllowedAsync(
        Guid tenantId,
        string canonicalModelId,
        CancellationToken cancellationToken = default)
    {
        var grants = await _grants.ListByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return ModelGrantEvaluator.IsModelAllowed(grants, canonicalModelId);
    }
}
