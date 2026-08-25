namespace Pol33.Persistence.Entities;

/// <summary>
/// Single-row store of the dashboard's process-lifetime aggregate counters. A single gateway
/// instance owns this row (see the single-instance assumption on the runtime state).
/// </summary>
public sealed class GatewayStatsSnapshotEntity
{
    /// <summary>Fixed primary key — there is only ever one row.</summary>
    public int Id { get; set; }

    public long TotalRequests { get; set; }

    public long TotalErrors { get; set; }

    public long ClientDisconnects { get; set; }

    public long TotalLatencyMs { get; set; }

    public long RateLimitRejections { get; set; }

    public long QuotaRejections { get; set; }

    /// <summary>Serialized per-model request counts (JSON object of model id → count).</summary>
    public string RequestsPerModelJson { get; set; } = "{}";

    /// <summary>Serialized per-model error counts (JSON object of model id → count).</summary>
    public string ErrorsPerModelJson { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; }
}
