using System.Text.RegularExpressions;

namespace Pol33.Core.Diagnostics;

/// <summary>
/// Strips credentials out of captured text before it is stored.
/// </summary>
/// <remarks>
/// Error records are read by every operator-tenant admin and can be exported to CSV, so anything
/// that reaches them is effectively published. Upstream error bodies and exception messages both
/// routinely echo the Authorization header back, so redaction happens at capture time rather than
/// at render time — a secret that is never written cannot leak from a later code path that forgets
/// to scrub.
/// <para>
/// Every pattern carries a 100 ms match timeout: these run on the request path against attacker-
/// influenced text, and an unbounded backtrack there is a denial of service.
/// </para>
/// </remarks>
public static partial class GatewayErrorRedactor
{
    private const string Mask = "***";
    private const string Ellipsis = "…";

    /// <summary>Redacts secrets and caps length. Returns null for null or blank input.</summary>
    public static string? Scrub(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var scrubbed = text;

        try
        {
            scrubbed = BearerPattern().Replace(scrubbed, "Bearer " + Mask);
            scrubbed = ApiKeyLiteralPattern().Replace(scrubbed, Mask);
            scrubbed = LabelledSecretPattern().Replace(scrubbed, "$1=" + Mask);
            scrubbed = UserInfoPattern().Replace(scrubbed, "://" + Mask + "@");
            scrubbed = QuerySecretPattern().Replace(scrubbed, "$1$2=" + Mask);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input beat the timeout. Storing partially-scrubbed text would be worse
            // than storing none, since the caller cannot tell the difference.
            return "[redacted: could not scrub within time limit]";
        }

        return maxLength > 0 && scrubbed.Length > maxLength
            ? string.Concat(scrubbed.AsSpan(0, maxLength), Ellipsis)
            : scrubbed;
    }

    /// <summary>
    /// Reduces a URL to <c>scheme://host[:port]/path</c>. Userinfo and the entire query string are
    /// dropped rather than pattern-matched — an unrecognized query parameter name is exactly how a
    /// key gets stored.
    /// </summary>
    public static string? ScrubUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            // Not parseable as a URL, so fall back to the text rules rather than storing it raw.
            return Scrub(url, 512);
        }

        var authority = uri.IsDefaultPort
            ? uri.Host
            : $"{uri.Host}:{uri.Port}";

        var path = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath;
        return $"{uri.Scheme}://{authority}{path}";
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-]+", RegexOptions.IgnoreCase, 100)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\bsk-(?:ant-)?[A-Za-z0-9_\-]{8,}", RegexOptions.None, 100)]
    private static partial Regex ApiKeyLiteralPattern();

    [GeneratedRegex(
        """\b(api[_-]?key|apikey|token|secret|password|authorization)\b\s*[:=]\s*"?'?[^\s"',}&]+""",
        RegexOptions.IgnoreCase,
        100)]
    private static partial Regex LabelledSecretPattern();

    [GeneratedRegex(@"://[^/@\s]+@", RegexOptions.None, 100)]
    private static partial Regex UserInfoPattern();

    [GeneratedRegex(@"([?&])(key|api_key|apikey|access_token|token)=[^&\s]+", RegexOptions.IgnoreCase, 100)]
    private static partial Regex QuerySecretPattern();
}
