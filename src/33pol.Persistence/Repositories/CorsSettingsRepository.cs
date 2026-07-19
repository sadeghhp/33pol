using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Repositories;

public sealed class CorsSettingsRepository(GatewayDbContext dbContext) : ICorsSettingsRepository
{
    private const int CorsSettingsRowId = 1;
    private const int ConfigVersionRowId = 1;

    public async Task<IReadOnlyList<string>?> GetAllowedOriginsAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.CorsSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == CorsSettingsRowId, cancellationToken)
            .ConfigureAwait(false);

        return row?.AllowedOrigins;
    }

    public async Task SaveAllowedOriginsAsync(
        IReadOnlyList<string> allowedOrigins,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);

        var now = DateTimeOffset.UtcNow;

        var settings = await dbContext.CorsSettings
            .FirstOrDefaultAsync(c => c.Id == CorsSettingsRowId, cancellationToken)
            .ConfigureAwait(false);

        if (settings is null)
        {
            settings = new CorsSettingsEntity { Id = CorsSettingsRowId };
            dbContext.CorsSettings.Add(settings);
        }

        settings.AllowedOrigins = allowedOrigins.ToList();
        settings.UpdatedAt = now;

        // Bump the config version in the same SaveChanges so the change and its version signal commit
        // atomically (a single SaveChangesAsync is one transaction across both rows).
        var version = await dbContext.ConfigVersions
            .FirstOrDefaultAsync(c => c.Id == ConfigVersionRowId, cancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            version = new ConfigVersionEntity { Id = ConfigVersionRowId, Version = 0 };
            dbContext.ConfigVersions.Add(version);
        }

        version.Version += 1;
        version.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
