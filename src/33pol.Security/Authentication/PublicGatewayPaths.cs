using Microsoft.AspNetCore.Http;

namespace Pol33.Security.Authentication;

public static class PublicGatewayPaths
{
    private static readonly string[] AnonymousPrefixes =
    [
        "/health",
        "/metrics",
        "/stats",
    ];

    /// <summary>
    /// Paths served without any credential: probes and the scrape endpoint.
    /// </summary>
    /// <remarks>
    /// Matched on whole path <em>segments</em>. Plain string prefix matching made every path merely
    /// beginning with those characters anonymous — <c>/metrics-internal</c>, <c>/statsdump</c>,
    /// <c>/healthz-admin</c> — so this set silently governed far more of the URL space than it
    /// names, and any future route sharing a prefix would have been exposed without authentication.
    /// </remarks>
    public static bool IsAnonymous(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value) || string.Equals(value, "/", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var prefix in AnonymousPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
