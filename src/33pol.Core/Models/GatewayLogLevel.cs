namespace Pol33.Core.Models;

/// <summary>
/// The severities the operator-facing log keeps. Deliberately coarser than
/// <c>Microsoft.Extensions.Logging.LogLevel</c>: trace/debug noise never reaches this buffer.
/// </summary>
public enum GatewayLogLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Critical = 3,
}

public static class GatewayLogLevels
{
    /// <summary>Maps a stored level name back onto the enum. Unknown names read as <see cref="GatewayLogLevel.Info"/>.</summary>
    public static GatewayLogLevel Parse(string? level) =>
        Enum.TryParse<GatewayLogLevel>(level, ignoreCase: true, out var parsed) ? parsed : GatewayLogLevel.Info;

    /// <summary>
    /// Parses the admin API's <c>?level=</c> filter into a severity floor. Empty, "all", and
    /// unrecognized values all mean "no floor" — a filter nobody understands should not hide rows.
    /// </summary>
    public static GatewayLogLevel? ParseFilter(string? level) =>
        string.IsNullOrWhiteSpace(level) || level.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : Enum.TryParse<GatewayLogLevel>(level, ignoreCase: true, out var parsed) ? parsed : null;
}
