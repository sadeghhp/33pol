using Microsoft.AspNetCore.Http;

namespace Pol33.App;

/// <summary>
/// Response headers for the admin console surface.
/// </summary>
/// <remarks>
/// The console is the most privileged surface the gateway exposes — API keys, upstream provider
/// secrets and pricing — so its assets are all served from this origin and the CSP says so. Every
/// script, style, font and connection is restricted to 'self', which means a compromised or spoofed
/// third-party origin has no way to execute in an admin session.
///
/// <c>script-src</c> stays free of 'unsafe-eval' because the console ships Alpine's CSP-friendly
/// build, which resolves directives as property paths rather than compiling them with
/// <c>new Function()</c>. Its markup therefore has to stay expression-free — see docs/admin-ui.md
/// and AdminAssetSecurityTests.AdminIndex_UsesOnlyExpressionsTheCspEvaluatorCanResolve.
/// </remarks>
internal static class AdminSecurityHeaders
{
    /// <summary>
    /// <c>style-src</c> allows 'unsafe-inline' and nothing else does.
    /// </summary>
    /// <remarks>
    /// The console's markup uses inline <c>style</c> attributes for a handful of layout tweaks, and
    /// Alpine's <c>x-show</c> toggles element visibility by writing <c>style="display:none"</c> at
    /// runtime — which CSP treats as an inline style. Removing it requires either nonce-stamping
    /// every attribute or replacing Alpine's visibility directives, neither of which is a safe
    /// change to make alongside a security fix. Inline <em>script</em> remains fully blocked, which
    /// is where the actual injection risk lies.
    /// </remarks>
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "object-src 'none'";

    public static void Apply(IHeaderDictionary headers)
    {
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
    }
}
