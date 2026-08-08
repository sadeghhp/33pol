using Microsoft.AspNetCore.Http;

namespace Pol33.Security.Authentication;

public static class PublicGatewayPaths
{
    private static readonly string[] AnonymousPrefixes =
    [
        "/health",
        "/metrics",
    ];

    /// <summary>
    /// Paths served without any credential: probes and the scrape endpoint.
    /// </summary>
    /// <remarks>
    /// Matched on whole path <em>segments</em>. Plain string prefix matching made every path merely
    /// beginning with those characters anonymous — <c>/metrics-internal</c>, <c>/statsdump</c>,
    /// <c>/healthz-admin</c> — so this set silently governed far more of the URL space than it
    /// names, and any future route sharing a prefix would have been exposed without authentication.
    ///
    /// <c>/stats</c> is deliberately absent. It returned the same snapshot the admin console gates
    /// behind an Admin key — per-model request and error counts, latency, active streams — so an
    /// anonymous caller could enumerate the model inventory and read the traffic profile. Probes
    /// need to answer up/down, not name the models.
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
