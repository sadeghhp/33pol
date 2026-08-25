namespace Pol33.Core.Models;

/// <summary>
/// Whether a model route is in service. <see cref="Serving"/> is the only state in which the
/// gateway will resolve the route: a <see cref="Stopped"/> route stays registered — with its
/// aliases, credential, pricing and grants intact — but is hidden from <c>GET /v1/models</c> and
/// refused at admission, so an operator can take a model out of service and put it back without
/// deleting and re-provisioning it.
/// </summary>
/// <remarks>
/// This is deliberately not backend health. Health is measured (a probe said the upstream is down
/// and the gateway will use it again when it recovers); state is declared (an operator said stop,
/// and only an operator can undo it).
/// </remarks>
public static class ModelRouteStates
{
    public const string Serving = "serving";

    public const string Stopped = "stopped";

    /// <summary>Every recognised state, in the order the admin UI lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Serving, Stopped];

    /// <summary>
    /// Strict parse for write paths: blank means "unspecified" and folds to <see cref="Serving"/>,
    /// but an unrecognised value is an error rather than a silently-serving route.
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = Serving;
            return true;
        }

        var trimmed = value.Trim();
        foreach (var candidate in All)
        {
            if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase))
            {
                normalized = candidate;
                return true;
            }
        }

        normalized = Serving;
        error = $"state must be one of: {string.Join(", ", All)}.";
        return false;
    }

    /// <summary>
    /// Tolerant parse for load paths. A route persisted by an older build carries no state at all,
    /// and a route carrying a value this build does not know must not vanish from the registry —
    /// both keep serving, which is what they were doing before this field existed.
    /// </summary>
    public static string Normalize(string? value) =>
        TryNormalize(value, out var normalized, out _) ? normalized : Serving;

    public static bool IsStopped(string? value) =>
        string.Equals(value?.Trim(), Stopped, StringComparison.OrdinalIgnoreCase);
}
