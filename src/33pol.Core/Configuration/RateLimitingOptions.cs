namespace Pol33.Core.Configuration;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Seed value for the global rate-limiting master switch. Only used to populate a fresh database;
    /// once seeded the live value comes from the config snapshot and is edited via the admin UI.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public RateLimitTierOptions Default { get; set; } = new();

    public Dictionary<string, RateLimitTierOptions> Plans { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, RateLimitTierOptions> Tenants { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A ceiling on all inference traffic through the gateway, whoever sends it. Left unset (zero
    /// rpm, zero streams) by default: it is a blunt instrument, and a gateway fronting several
    /// backends usually wants per-model limits instead. Set it when the gateway process itself — not
    /// any one upstream — is the resource you are protecting.
    /// </summary>
    public RateLimitTierOptions Global { get; set; } = RateLimitTierOptions.Unset();

    /// <summary>
    /// Per-model limits, keyed by canonical model id (aliases resolve to it before the rule is
    /// looked up). Summed across every caller, so this is the model's own capacity rather than any
    /// one tenant's share of it.
    /// </summary>
    public Dictionary<string, RateLimitTierOptions> Models { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-API-key limits, keyed by key id. Bounds one credential inside its tenant's allowance, so
    /// a runaway or compromised key cannot spend the whole tenant budget.
    /// </summary>
    public Dictionary<string, RateLimitTierOptions> ApiKeys { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Combined per-tenant-per-model limits, keyed <c>tenantId|modelId</c>. The narrowest limit an
    /// operator normally needs: "this customer may have 20 rpm of the expensive model, and its usual
    /// allowance of everything else".
    /// </summary>
    public Dictionary<string, RateLimitTierOptions> TenantModels { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Combined per-key-per-model limits, keyed <c>apiKeyId|modelId</c>.</summary>
    public Dictionary<string, RateLimitTierOptions> ApiKeyModels { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The budget for requests that fail authentication, counted per client address block.
    /// </summary>
    /// <remarks>
    /// Its own tier because the default one is not the right shape for it: a default sized for
    /// legitimate traffic (hundreds of rpm) lets one address make hundreds of credential guesses a
    /// minute, which is not a rate limit on guessing in any useful sense. Left at zero rpm the
    /// gateway falls back to the default tier, preserving the behaviour deployments have today.
    /// </remarks>
    public RateLimitTierOptions AuthFailure { get; set; } = new()
    {
        Rpm = 60,
        Burst = 20,
        MaxConcurrentStreams = 0,
    };

    public AdaptiveRateLimitOptions Adaptive { get; set; } = new();

    public int InMemoryPartitionRetentionSeconds { get; set; } = 3600;

    /// <summary>
    /// How often the maintenance service sweeps stale partitions.
    /// </summary>
    /// <remarks>
    /// Sweeping used to happen inline, on whichever request happened to trip an operation counter:
    /// that request paid an O(live partitions) scan, and past the partition ceiling it also paid a
    /// full copy-and-sort of the table. At the default ceiling that is a 50,000-element allocation
    /// and sort on a request thread — invisible in an average, and a very visible p99. A timer costs
    /// the same work at a rate set by wall-clock instead of by traffic, and no request waits for it.
    /// </remarks>
    public int MaintenanceIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Hard ceiling on live partitions per dimension (request buckets, stream-slot states). Anonymous
    /// traffic partitions by client address block, so without a ceiling a caller spread across an
    /// address range mints one entry per block and holds it for the whole retention window. When the
    /// ceiling is passed, the least-recently-seen partitions are evicted down to it. Evicting a
    /// partition resets its bucket, so keep this comfortably above the number of clients you expect.
    /// </summary>
    public int InMemoryMaxPartitions { get; set; } = 50_000;

    /// <summary>
    /// How many distinct keys the usage report tracks per section. Past it, new keys are ignored
    /// rather than evicting existing ones, so a flood of one-off callers cannot push the tenants an
    /// operator is watching out of the report.
    /// </summary>
    public int UsageReportMaxKeys { get; set; } = 500;
}

/// <summary>
/// Bounds and gains for load-aware enforcement. Every one of them is a limit on how far the governor
/// may move, never on how far a configured tier may be exceeded — the factor is capped at 1.0 in the
/// governor and again where it is applied.
/// </summary>
public sealed class AdaptiveRateLimitOptions
{
    /// <summary>
    /// Master switch for adaptation. Off by default: a gateway should enforce exactly what its
    /// operator configured until that operator asks for something cleverer.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The furthest a model's rules may be scaled down. At 0.25 a saturated model still admits a
    /// quarter of its configured rate, so adaptation degrades service rather than causing an outage.
    /// </summary>
    public double MinFactor { get; set; } = 0.25;

    /// <summary>Saturation at or above which a model's factor is cut. Below <see cref="LowWatermark"/> it recovers; between the two it holds, which is what stops it oscillating.</summary>
    public double HighWatermark { get; set; } = 0.85;

    /// <summary>Saturation at or below which a model's factor recovers towards 1.0.</summary>
    public double LowWatermark { get; set; } = 0.5;

    /// <summary>Multiplicative cut applied per evaluation while a model is over the high watermark.</summary>
    public double DecreaseFactor { get; set; } = 0.8;

    /// <summary>Additive recovery applied per evaluation while a model is under the low watermark.</summary>
    public double IncreaseStep { get; set; } = 0.05;

    /// <summary>
    /// Consecutive refusals a partition must accumulate before its <c>Retry-After</c> starts
    /// escalating. Set above a handful so an ordinary burst that briefly overruns its tier is told to
    /// wait the usual second.
    /// </summary>
    public int BackoffAfterConsecutiveRejections { get; set; } = 5;

    /// <summary>Multiplier applied per rejection past the threshold, compounding up to the ceiling.</summary>
    public double BackoffGrowthFactor { get; set; } = 1.6;

    /// <summary>
    /// Longest <c>Retry-After</c> the gateway will ever send. Clients sleep for this header, so an
    /// unbounded value is indistinguishable from a hang.
    /// </summary>
    public int MaxRetryAfterSeconds { get; set; } = 60;

    /// <summary>
    /// Share of the computed <c>Retry-After</c> spread randomly, in <c>[0, 1)</c>. Without it every
    /// client refused in the same second returns in the same later second, and the gateway
    /// synchronises its own thundering herd.
    /// </summary>
    public double RetryAfterJitter { get; set; } = 0.2;
}

public sealed class RateLimitTierOptions
{
    public int Rpm { get; set; } = 600;

    public int Burst { get; set; } = 100;

    /// <summary>Concurrent streaming responses allowed per partition. Zero means unlimited.</summary>
    public int MaxConcurrentStreams { get; set; } = 50;

    /// <summary>A tier that enforces nothing — the shape an unset optional scope takes.</summary>
    public static RateLimitTierOptions Unset() => new() { Rpm = 0, Burst = 0, MaxConcurrentStreams = 0 };

    public bool EnforcesNothing => Rpm + Burst <= 0 && MaxConcurrentStreams <= 0;
}
