namespace Pol33.Core.Configuration;

/// <summary>
/// Matches request origins against configured exact origins and subdomain wildcard patterns
/// (e.g. <c>https://*.github.io</c>).
/// </summary>
public static class CorsOriginMatcher
{
    private const string SchemeDelimiter = "://";

    public static bool IsOriginAllowed(string? requestOrigin, IReadOnlyList<string> allowedPatterns)
    {
        if (string.IsNullOrWhiteSpace(requestOrigin) ||
            !Uri.TryCreate(requestOrigin, UriKind.Absolute, out var originUri) ||
            (originUri.Scheme != Uri.UriSchemeHttp && originUri.Scheme != Uri.UriSchemeHttps) ||
            !string.Equals(requestOrigin, originUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var pattern in allowedPatterns)
        {
            if (MatchesPattern(originUri, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPattern(Uri originUri, string pattern)
    {
        if (pattern.Contains('*', StringComparison.Ordinal))
        {
            return MatchesWildcardPattern(originUri, pattern);
        }

        return Uri.TryCreate(pattern, UriKind.Absolute, out var exactUri) &&
               OriginsEqual(originUri, exactUri);
    }

    private static bool OriginsEqual(Uri left, Uri right)
    {
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Authority, right.Authority, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesWildcardPattern(Uri originUri, string pattern)
    {
        var delimiterIndex = pattern.IndexOf(SchemeDelimiter, StringComparison.Ordinal);
        if (delimiterIndex < 0)
        {
            return false;
        }

        var scheme = pattern[..delimiterIndex];
        if (!string.Equals(originUri.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hostPattern = pattern[(delimiterIndex + SchemeDelimiter.Length)..].TrimEnd('/');
        if (!hostPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        var domainSuffix = hostPattern[2..];
        if (domainSuffix.Length == 0 || domainSuffix.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        // "*.example.com:8443" — the suffix is compared against the host, the port against the
        // origin's port. Uri.Host never carries the port, so without splitting it the pattern was
        // accepted by validation and then silently matched nothing.
        var portIndex = domainSuffix.IndexOf(':', StringComparison.Ordinal);
        if (portIndex >= 0)
        {
            if (!int.TryParse(domainSuffix[(portIndex + 1)..], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var patternPort) ||
                originUri.Port != patternPort)
            {
                return false;
            }

            domainSuffix = domainSuffix[..portIndex];
        }

        var host = originUri.Host;
        if (!host.EndsWith('.' + domainSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = host[..^(domainSuffix.Length + 1)];
        return prefix.Length > 0;
    }
}
