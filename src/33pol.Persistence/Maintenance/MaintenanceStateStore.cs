using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Maintenance;

public sealed class MaintenanceStateStore(GatewayDbContext dbContext, TimeProvider? timeProvider = null) : IMaintenanceStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var row = await dbContext.MaintenanceState
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.Key == key, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(row.ValueJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        var json = JsonSerializer.Serialize(value, Json);
        var now = _time.GetUtcNow();

        var row = await dbContext.MaintenanceState
            .SingleOrDefaultAsync(m => m.Key == key, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            dbContext.MaintenanceState.Add(new MaintenanceStateEntity { Key = key, ValueJson = json, UpdatedAt = now });
        }
        else
        {
            row.ValueJson = json;
            row.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
