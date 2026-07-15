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
                error = "Wildcard origin '*' is not allowed; list exact origins.";
                return false;
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
}
