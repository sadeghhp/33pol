using Microsoft.AspNetCore.Http;
using Pol33.Core.Models;
using Pol33.Core.Security;

namespace Pol33.Security.Authentication;

public static class PublicModelAccess
{
    /// <summary>
    /// True when a credential the gateway recognises was presented on this request and rejected.
    /// </summary>
    /// <remarks>
    /// The anonymous paths below exist for callers with no usable credential. A caller who presents
    /// a revoked or expired key — one that matched a stored record — must get the authentication
    /// error, not a silent downgrade to anonymous access that answers 200 and hides the fact that
    /// their key stopped working. A key that matches nothing is not flagged here: it is a
    /// placeholder, indistinguishable in effect from sending no key, and authentication lets it
    /// through as anonymous rather than refusing a request the route would have served regardless.
    /// </remarks>
    public static bool HasRejectedCredential(HttpContext context) =>
        context.Items.ContainsKey(GatewayAuthContextItems.AuthFailureCode);

    public static bool IsPublicInferenceRequest(HttpContext context) =>
        context.Items.TryGetValue(PublicModelAccessKeys.IsPublicInference, out var value) &&
        value is true;

    public static string? GetCanonicalModelId(HttpContext context) =>
        context.Items.TryGetValue(PublicModelAccessKeys.CanonicalModelId, out var value)
            ? value as string
            : null;

    public static bool AllowsAnonymousModelsListing(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        return path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/v1/models/", StringComparison.OrdinalIgnoreCase);
    }
}
