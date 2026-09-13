using Microsoft.AspNetCore.Http;

namespace Pol33.Proxy.Routing;

public static class InferenceRouteClassifier
{
    private static readonly string[] PassthroughPrefixes =
    [
        "/health",
        "/stats",
        "/metrics",
        "/admin",
        "/v1/models",
    ];

    /// <summary>
    /// The inference paths the gateway forwards. Matched exactly, never by suffix.
    /// </summary>
    /// <remarks>
    /// Suffix matching let any prefix reach the router — <c>/x/v1/chat/completions</c> classified as
    /// routable inference. Authorization selects its policy by <em>prefix</em>
    /// (<c>path.StartsWith("/v1/")</c>), so such a request matched no policy and skipped policy
    /// evaluation entirely, while the router still forwarded it. The router's own inline check only
    /// confirms that tenant and key claims exist, not the key's role, so an admin-only key could
    /// perform inference through the prefixed path though it is refused on the real one. Anchoring
    /// both ends to the same exact set removes the divergence; it also means the forwarded path can
    /// only ever be one of these, so no client-supplied segment reaches the upstream URL builder.
    /// </remarks>
    private static readonly string[] RoutablePaths =
    [
        "/v1/chat/completions",
        "/v1/completions",
        "/v1/embeddings",
        "/v1/rerank",
    ];

    public static bool IsPassthroughPath(PathString path)
    {
        foreach (var prefix in PassthroughPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The gateway's own request-serving surface: the admin API and the model listing.
    /// </summary>
    /// <remarks>
    /// Metered by <c>RateLimitMiddleware</c> against a per-caller budget of its own. Neither is
    /// forwarded upstream, so neither is inference — but both are real work (several admin reads hit
    /// the database), and until they were metered a key could poll them without any ceiling at all.
    /// The static console under <c>/admin</c> is not included: it is files, served by the static
    /// file middleware, and rate-limiting a page load would only break the console.
    /// </remarks>
    public static bool IsControlPlane(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments(AdminApiPrefix, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments(ModelsPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private const string AdminApiPrefix = "/admin/api";
    private const string ModelsPrefix = "/v1/models";

    public static bool IsRoutableInference(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return false;
        }

        return IsRoutablePath(context.Request.Path);
    }

    public static bool IsRoutablePath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Tolerate exactly one trailing slash; anything else must match verbatim.
        if (value.Length > 1 && value[^1] == '/')
        {
            value = value[..^1];
        }

        foreach (var routable in RoutablePaths)
        {
            if (string.Equals(value, routable, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
