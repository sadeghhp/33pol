using System.Text;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class RateCardRepository(GatewayDbContext dbContext) : IRateCardRepository
{
    private const string DefaultCurrency = "USD";
    private const int SlugMaxLength = 64;

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

    public Task<RateCardRecord?> GetForModelAsync(
        string modelId,
        CancellationToken cancellationToken = default) =>
        GetActiveForModelAsync(modelId, DateTimeOffset.UtcNow, cancellationToken);

    public async Task<IReadOnlyDictionary<string, RateCardRecord>> GetActiveByModelAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var entities = await dbContext.RateCards
            .AsNoTracking()
            .Where(r =>
                r.IsActive &&
                r.EffectiveFrom <= now &&
                (r.EffectiveUntil == null || r.EffectiveUntil > now))
            .OrderByDescending(r => r.EffectiveFrom)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, RateCardRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            // Ordered by EffectiveFrom descending, so the first entry per model is the current one.
            if (!result.ContainsKey(entity.ModelId))
            {
                result[entity.ModelId] = BillingEntityMapper.ToRecord(entity);
            }
        }

        return result;
    }

    public async Task UpsertForModelAsync(
        string modelId,
        decimal inputPricePerMillionTokens,
        decimal outputPricePerMillionTokens,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var existing = await dbContext.RateCards
            .Where(r =>
                r.ModelId == modelId &&
                r.IsActive &&
                r.EffectiveFrom <= now &&
                (r.EffectiveUntil == null || r.EffectiveUntil > now))
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.InputPricePerMillionTokens = inputPricePerMillionTokens;
            existing.OutputPricePerMillionTokens = outputPricePerMillionTokens;
            existing.UpdatedAt = now;
        }
        else
        {
            var slug = await ReserveSlugAsync(modelId, cancellationToken).ConfigureAwait(false);
            dbContext.RateCards.Add(new RateCardEntity
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                Name = modelId,
                ModelId = modelId,
                InputPricePerMillionTokens = inputPricePerMillionTokens,
                OutputPricePerMillionTokens = outputPricePerMillionTokens,
                Currency = DefaultCurrency,
                EffectiveFrom = now,
                EffectiveUntil = null,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteForModelAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        // RemoveRange rather than ExecuteDelete: the InMemory provider used in tests lacks bulk delete.
        var entities = await dbContext.RateCards
            .Where(r => r.ModelId == modelId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (entities.Count == 0)
        {
            return;
        }

        dbContext.RateCards.RemoveRange(entities);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a slug from the model id that does not collide with an existing one.
    /// Slug carries a unique index, and model ids may contain characters (slashes, colons) that
    /// do not belong in a slug.
    /// </summary>
    private async Task<string> ReserveSlugAsync(string modelId, CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(modelId);

        var taken = await dbContext.RateCards
            .AsNoTracking()
            .Where(r => r.Slug == baseSlug || r.Slug.StartsWith(baseSlug + "-"))
            .Select(r => r.Slug)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var takenSet = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!takenSet.Contains(baseSlug))
        {
            return baseSlug;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{Truncate(baseSlug, SlugMaxLength - 1 - suffix.ToString().Length)}-{suffix}";
            if (!takenSet.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;

        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        slug = Truncate(slug, SlugMaxLength).Trim('-');

        return slug.Length == 0 ? "rate-card" : slug;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
