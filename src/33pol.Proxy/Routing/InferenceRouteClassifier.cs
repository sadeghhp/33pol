using Microsoft.AspNetCore.Http;

namespace Pol33.Proxy.Routing;

public static class InferenceRouteClassifier
{
    private static readonly string[] PassthroughPrefixes =
    [
        "/health",
        "/stats",
        "/metrics",
        "/admin/",
        "/admin/api/",
        "/v1/models",
    ];

    private static readonly string[] RoutableSuffixes =
    [
        "/v1/chat/completions",
        "/v1/completions",
        "/v1/embeddings",
    ];

    public static bool IsPassthroughPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var prefix in PassthroughPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsRoutableInference(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var suffix in RoutableSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
