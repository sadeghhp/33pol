using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class RateCardRepository(GatewayDbContext dbContext) : IRateCardRepository
{
    public async Task<RateCardRecord?> GetActiveForModelAsync(
        string modelId,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.RateCards
            .AsNoTracking()
            .Where(r =>
                r.ModelId == modelId &&
                r.IsActive &&
                r.EffectiveFrom <= atUtc &&
                (r.EffectiveUntil == null || r.EffectiveUntil > atUtc))
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : BillingEntityMapper.ToRecord(entity);
    }
}
