using Pol33.Core.Configuration;

namespace Pol33.App.DependencyInjection;

/// <summary>
/// Startup budget for the database-backed configuration snapshot. Bound from the same section as
/// <see cref="GatewayConfigSnapshotOptions"/> (<c>Gateway:ConfigSnapshot</c>).
/// </summary>
public sealed class GatewayConfigSnapshotStartupOptions
{
    public const string SectionName = GatewayConfigSnapshotOptions.SectionName;

    /// <summary>
    /// Total time the host waits for the first snapshot before failing startup. The per-attempt
    /// backoff is still capped by <see cref="GatewayConfigSnapshotOptions.InitialLoadMaxBackoffSeconds"/>.
    /// </summary>
    public int InitialLoadTimeoutSeconds { get; set; } = 60;
}

/// <summary>Raised when the initial configuration load exhausts its startup budget.</summary>
public sealed class GatewayConfigStartupException(string message) : Exception(message);
