namespace Pol33.Core.Configuration;

/// <summary>
/// Controls the durable error store behind the admin Errors tab: how much is held in memory, how
/// much reaches the database, and how long it is kept.
/// </summary>
public sealed class GatewayErrorTrackingOptions
{
    public const string SectionName = "Gateway:ErrorTracking";

    /// <summary>Turns capture off entirely. The endpoints stay mapped and simply report nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Occurrences held in memory. Serves reads when no database is configured.</summary>
    public int HotBufferCapacity { get; set; } = 1000;

    /// <summary>
    /// Distinct failures whose running totals are kept. Aggregates outlive the ring, so a fault
    /// that fired 50,000 times still reports its true count and first-seen after the individual
    /// occurrences have been evicted.
    /// </summary>
    public int MaxTrackedFingerprints { get; set; } = 500;

    /// <summary>When false, errors stay in memory and are lost on restart even with a database configured.</summary>
    public bool PersistToDatabase { get; set; } = true;

    public int WriterBatchSize { get; set; } = 50;

    public int WriterFlushIntervalMs { get; set; } = 5000;

    public int RetentionDays { get; set; } = 14;

    public int MaxRows { get; set; } = 100_000;

    public int PruneIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Buffers small upstream error bodies so the console can show what the model server actually
    /// said. Off by default: it is the one option here that touches the client-visible response
    /// path.
    /// </summary>
    public bool CaptureUpstreamBodySnippet { get; set; }

    public int UpstreamBodySnippetBytes { get; set; } = 2048;

    public int MaxStackTraceLength { get; set; } = 8000;

    public int MaxMessageLength { get; set; } = 1000;

    /// <summary>
    /// Logger categories never mirrored into the error store or the Logs buffer. Kestrel and the
    /// forwarder log a warning for every client that drops a connection mid-response; without this
    /// they would evict every real diagnostic from the ring.
    /// </summary>
    public IList<string> IgnoredCategories { get; set; } =
    [
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Yarp.ReverseProxy.Forwarder",
    ];

    /// <summary>
    /// Components that publish their own error records, with the model, upstream and outcome
    /// attached. Their log lines still reach the Logs buffer, but the sink does not mirror them into
    /// the error store — the detailed record it would duplicate is strictly better, and the log copy
    /// is written first, so leaving it in means the Errors tab leads with the thinner of the two.
    /// </summary>
    /// <remarks>Matched against the short category name the sink stores, not the full namespace.</remarks>
    public IList<string> SelfReportingCategories { get; set; } =
    [
        "ModelRouterMiddleware",
        "GatewayExceptionHandlingMiddleware",

        // Serilog's request-completion line logs at Error for any 5xx response. It restates a
        // failure the proxy has already recorded in full, and carries no model, upstream or
        // request id of its own — useful context in the log tail, but not a distinct error.
        "RequestLoggingMiddleware",
    ];
}
