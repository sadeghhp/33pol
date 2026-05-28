using Microsoft.AspNetCore.Http;
using Pol33.Core.Models;

namespace Pol33.Security.Authentication;

public static class PublicModelAccess
{
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
