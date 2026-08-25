using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Repositories;

public sealed class GatewayStatsSnapshotStore(GatewayDbContext dbContext) : IGatewayStatsSnapshotStore
{
    // The counters row is a singleton; the fixed key keeps it a pure upsert.
    private const int SnapshotRowId = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GatewayRuntimeSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var counters = await dbContext.GatewayStatsSnapshot
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SnapshotRowId, cancellationToken)
            .ConfigureAwait(false);

        if (counters is null)
        {
            return null;
        }

        var recent = await dbContext.RecentRequests
            .AsNoTracking()
            .OrderBy(r => r.TimestampUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new GatewayRuntimeSnapshot
        {
            TotalRequests = counters.TotalRequests,
            TotalErrors = counters.TotalErrors,
            ClientDisconnects = counters.ClientDisconnects,
            TotalLatencyMs = counters.TotalLatencyMs,
            RateLimitRejections = counters.RateLimitRejections,
            QuotaRejections = counters.QuotaRejections,
            RequestsPerModel = Deserialize(counters.RequestsPerModelJson),
            ErrorsPerModel = Deserialize(counters.ErrorsPerModelJson),
            Recent = recent.Select(ToEntry).ToList(),
        };
    }

    public async Task SaveAsync(GatewayRuntimeSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var now = DateTimeOffset.UtcNow;

        var counters = await dbContext.GatewayStatsSnapshot
            .FirstOrDefaultAsync(s => s.Id == SnapshotRowId, cancellationToken)
            .ConfigureAwait(false);

        if (counters is null)
        {
            counters = new GatewayStatsSnapshotEntity { Id = SnapshotRowId };
            dbContext.GatewayStatsSnapshot.Add(counters);
        }

        counters.TotalRequests = snapshot.TotalRequests;
        counters.TotalErrors = snapshot.TotalErrors;
        counters.ClientDisconnects = snapshot.ClientDisconnects;
        counters.TotalLatencyMs = snapshot.TotalLatencyMs;
        counters.RateLimitRejections = snapshot.RateLimitRejections;
        counters.QuotaRejections = snapshot.QuotaRejections;
        counters.RequestsPerModelJson = Serialize(snapshot.RequestsPerModel);
        counters.ErrorsPerModelJson = Serialize(snapshot.ErrorsPerModel);
        counters.UpdatedAt = now;

        // The recent feed is a bounded rolling window (~500 rows); replace it wholesale rather than
        // diff it. RemoveRange keeps this provider-agnostic (the EF InMemory provider used by tests
        // does not support ExecuteDelete).
        var existingRecent = await dbContext.RecentRequests
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.RecentRequests.RemoveRange(existingRecent);
        foreach (var entry in snapshot.Recent)
        {
            dbContext.RecentRequests.Add(ToEntity(entry));
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Serialize(IReadOnlyDictionary<string, long> value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static IReadOnlyDictionary<string, long> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, long>>(json, JsonOptions);
        return parsed is null
            ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    private static RecentRequestSnapshotEntity ToEntity(RecentRequestEntry entry) => new()
    {
        RequestId = entry.RequestId,
        Method = entry.Method,
        Path = entry.Path,
        ModelId = entry.ModelId,
        TenantId = entry.TenantId,
        StatusCode = entry.StatusCode,
        DurationMs = entry.DurationMs,
        IsStreaming = entry.IsStreaming,
        ErrorCode = entry.ErrorCode,
        TimestampUtc = entry.TimestampUtc,
        CostCenter = entry.CostCenter,
        PromptTokens = entry.PromptTokens,
        CompletionTokens = entry.CompletionTokens,
        TotalTokens = entry.TotalTokens,
        TokenSource = entry.TokenSource,
        InputCost = entry.InputCost,
        OutputCost = entry.OutputCost,
        TotalCost = entry.TotalCost,
        Currency = entry.Currency,
        PricingStatus = entry.PricingStatus,
    };

    private static RecentRequestEntry ToEntry(RecentRequestSnapshotEntity entity) => new()
    {
        RequestId = entity.RequestId,
        Method = entity.Method,
        Path = entity.Path,
        ModelId = entity.ModelId,
        TenantId = entity.TenantId,
        StatusCode = entity.StatusCode,
        DurationMs = entity.DurationMs,
        IsStreaming = entity.IsStreaming,
        ErrorCode = entity.ErrorCode,
        TimestampUtc = entity.TimestampUtc,
        CostCenter = entity.CostCenter,
        PromptTokens = entity.PromptTokens,
        CompletionTokens = entity.CompletionTokens,
        TotalTokens = entity.TotalTokens,
        TokenSource = entity.TokenSource,
        InputCost = entity.InputCost,
        OutputCost = entity.OutputCost,
        TotalCost = entity.TotalCost,
        Currency = entity.Currency,
        PricingStatus = entity.PricingStatus,
    };
}
