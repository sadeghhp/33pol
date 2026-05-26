using Microsoft.AspNetCore.Http;

namespace Pol33.Security.Authentication;

public static class PublicGatewayPaths
{
    private static readonly string[] AnonymousPrefixes =
    [
        "/health",
        "/health/live",
        "/health/ready",
        "/metrics",
        "/stats",
    ];

    public static bool IsAnonymous(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return value == "/";
        }

        if (string.Equals(value, "/", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var prefix in AnonymousPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
