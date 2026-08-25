using System.Text.Json.Serialization;
using Pol33.Core.Models.Overview;

namespace Pol33.Core.Models;

public sealed class AdminSummarySnapshot
{
    public required string Uptime { get; init; }

    public long UptimeSeconds { get; init; }

    public long TotalInferenceRequests { get; init; }

    public long TotalErrors { get; init; }

    /// <summary>
    /// Requests whose client disconnected before the response finished. Reported beside
    /// <see cref="TotalErrors"/> rather than inside it, so the Overview pill and the Errors tab count
    /// the same population.
    /// </summary>
    public long ClientDisconnects { get; init; }

    public double AverageLatencyMs { get; init; }

    public int ActiveStreams { get; init; }

    /// <summary>
    /// Inference requests currently being forwarded, streaming or not. <see cref="ActiveStreams"/>
    /// is the streaming subset of this, so a non-streaming call in progress moves this and not that.
    /// </summary>
    public int ActiveRequests { get; init; }

    /// <summary>Per-model breakdown of <see cref="ActiveRequests"/>; models at zero are omitted.</summary>
    public IReadOnlyDictionary<string, int> ActiveRequestsPerModel { get; init; } =
        new Dictionary<string, int>();

    public long RateLimitRejections { get; init; }

    public long QuotaRejections { get; init; }

    public IReadOnlyDictionary<string, long> RequestsPerModel { get; init; } =
        new Dictionary<string, long>();

    public IReadOnlyDictionary<string, long> ErrorsPerModel { get; init; } =
        new Dictionary<string, long>();

    // ---- Overview sections (all optional; null when the producing component is not wired) ----

    /// <summary>Trailing windows (1m, 5m, 1h, 24h) with percentiles, tokens, cost and rejections.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WindowStats>? Windows { get; init; }

    /// <summary>One point per minute for the last hour, for the sparklines.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OverviewSeries? Series { get; init; }

    /// <summary>Per-model routing health: probe result, circuit breaker and bulkhead occupancy.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BackendOverview>? Backends { get; init; }

    /// <summary>Ranked list of conditions an operator should act on; empty when all is well.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AttentionItem>? Attention { get; init; }

    /// <summary>Usage/billing pipeline health.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PipelineOverview? Pipeline { get; init; }

    /// <summary>In-memory policy pressure (rejections by reason / tenant / model).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PolicyLiveOverview? Policy { get; init; }

    /// <summary>Process and config facts cheap enough to refresh with every frame.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ControlPlaneLiveOverview? ControlPlane { get; init; }
}
