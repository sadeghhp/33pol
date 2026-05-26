using Microsoft.AspNetCore.Http;

namespace Pol33.Security.Authentication;

public static class ApiKeyPathClassifier
{
    private static readonly string[] PublicPrefixes =
    [
        "/health",
        "/metrics",
        "/stats",
    ];

    public static bool IsPublicPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var prefix in PublicPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsAdminPath(PathString path)
    {
        var value = path.Value;
        return !string.IsNullOrEmpty(value) &&
               value.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool RequiresInferenceKey(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase);
    }
}
