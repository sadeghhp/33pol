namespace Pol33.Core.Configuration;

/// <summary>
/// Validates admin-managed CORS allowed origins.
/// </summary>
public static class CorsConfigValidation
{
    public const int MaxOrigins = 100;
    public const int MaxOriginLength = 256;

    public static bool TryValidate(IEnumerable<string>? origins, out string? error, out string[] normalized)
    {
        error = null;
        normalized = [];

        if (origins is null)
        {
            error = "allowedOrigins is required.";
            return false;
        }

        var list = origins.ToList();
        if (list.Count > MaxOrigins)
        {
            error = $"allowedOrigins cannot exceed {MaxOrigins} entries.";
            return false;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var raw = list[i];
            if (raw is null)
            {
                error = $"allowedOrigins[{i}] is required.";
                return false;
            }

            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                // Blank entries are dropped by NormalizeOrigins; skip validation noise.
                continue;
            }

            if (trimmed.Length > MaxOriginLength)
            {
                error = $"allowedOrigins[{i}] exceeds {MaxOriginLength} characters.";
                return false;
            }

            if (trimmed == "*")
            {
                error = "Wildcard origin '*' is not allowed; list exact origins or subdomain patterns (e.g. https://*.github.io).";
                return false;
            }

            if (trimmed.Contains('*'))
            {
                if (!TryValidateWildcardOrigin(trimmed, i, out error))
                {
                    return false;
                }

                continue;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = $"allowedOrigins[{i}] must be an absolute http or https origin.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            {
                error = $"allowedOrigins[{i}] must not include a path (use origin only, e.g. https://app.example.com).";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = $"allowedOrigins[{i}] must not include query or fragment.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                error = $"allowedOrigins[{i}] must not include user info.";
                return false;
            }
        }

        normalized = GatewayCorsOptions.NormalizeOrigins(list);
        return true;
    }

    private static bool TryValidateWildcardOrigin(string trimmed, int index, out string? error)
    {
        error = null;

        const string delimiter = "://";
        var delimiterIndex = trimmed.IndexOf(delimiter, StringComparison.Ordinal);
        if (delimiterIndex < 0)
        {
            error = $"allowedOrigins[{index}] wildcard pattern must be an absolute http or https origin pattern.";
            return false;
        }

        var scheme = trimmed[..delimiterIndex];
        if (!string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
            !string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            error = $"allowedOrigins[{index}] must use http or https scheme.";
            return false;
        }

        var hostPattern = trimmed[(delimiterIndex + delimiter.Length)..].TrimEnd('/');
        if (!hostPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            error =
                $"allowedOrigins[{index}] wildcard must be a subdomain pattern (e.g. https://*.github.io).";
            return false;
        }

        if (hostPattern.Length > 1 && hostPattern.IndexOf('*', 1) >= 0)
        {
            error = $"allowedOrigins[{index}] supports only one subdomain wildcard (*.suffix).";
            return false;
        }

        var suffix = hostPattern[2..];
        if (suffix.Length == 0 || suffix.Contains('*') || suffix.Contains('/'))
        {
            error = $"allowedOrigins[{index}] wildcard suffix is invalid.";
            return false;
        }

        // An optional ":port" is matched against the origin's port by CorsOriginMatcher; anything
        // else after a colon can never match, so refuse it here rather than accept a dead pattern.
        var portIndex = suffix.IndexOf(':', StringComparison.Ordinal);
        if (portIndex >= 0)
        {
            var port = suffix[(portIndex + 1)..];
            if (port.Length == 0 || port.Contains(':') ||
                !int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedPort) ||
                parsedPort is < 1 or > 65535)
            {
                error = $"allowedOrigins[{index}] wildcard port must be a number between 1 and 65535 (e.g. https://*.example.com:8443).";
                return false;
            }

            suffix = suffix[..portIndex];
        }

        if (!suffix.Contains('.'))
        {
            error = $"allowedOrigins[{index}] wildcard suffix must be a valid domain.";
            return false;
        }

        return true;
    }
}
