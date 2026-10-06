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
/// <c>script-src</c> stays free of 'unsafe-eval' because the console is a compiled SolidJS bundle
/// with no inline scripts. See AdminSolidInvariantTests for SPA shell invariants.
/// </remarks>
internal static class AdminSecurityHeaders
{
    /// <summary>
    /// <c>style-src 'self'</c> — the Solid bundle uses classes only; no inline styles in the SPA shell.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
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
