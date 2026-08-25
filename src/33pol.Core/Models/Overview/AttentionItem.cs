namespace Pol33.Core.Models.Overview;

/// <summary>
/// Something an operator should look at now. Codes and thresholds mirror the Prometheus rules under
/// <c>deploy/prometheus/alerts</c>; the in-app list exists so the console is useful without a
/// monitoring stack, not instead of one.
/// </summary>
public sealed record AttentionItem
{
    public const string SeverityCritical = "critical";
    public const string SeverityWarning = "warning";
    public const string SeverityInfo = "info";

    public required string Severity { get; init; }

    /// <summary>Stable machine code, e.g. <c>circuit_open</c>.</summary>
    public required string Code { get; init; }

    public required string Title { get; init; }

    public string Detail { get; init; } = string.Empty;

    /// <summary>When the condition was first observed by this process.</summary>
    public DateTimeOffset SinceUtc { get; init; }

    public string? ModelId { get; init; }

    public string? TenantId { get; init; }

    /// <summary>Where in the console to go; null when there is no more specific page.</summary>
    public AttentionLink? Link { get; init; }
}

/// <summary>A console deep link: the tab plus the hash parameters it understands.</summary>
public sealed record AttentionLink(string Tab, IReadOnlyDictionary<string, string>? Params = null);
