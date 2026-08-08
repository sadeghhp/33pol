using Microsoft.AspNetCore.Http;

namespace Pol33.Proxy.Routing;

public static class InferenceDestinationBuilder
{
    /// <summary>
    /// Normalizes a model upstream base URL for YARP forwarding so path segments such as
    /// <c>/v1/chat/completions</c> append under <c>/api/</c> rather than replacing it.
    /// </summary>
    public static string ToForwarderDestination(string modelUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelUrl);
        return modelUrl.EndsWith('/') ? modelUrl : modelUrl + "/";
    }

    public static Uri BuildOutboundUri(string destinationPrefix, PathString path, QueryString query)
    {
        var baseUri = new Uri(ToForwarderDestination(destinationPrefix), UriKind.Absolute);
        var relative = path.HasValue ? path.Value!.TrimStart('/') : string.Empty;
        if (query.HasValue)
        {
            relative += query.Value;
        }

        var resolved = new Uri(baseUri, relative);

        // Defense-in-depth: if the "relative" segment was actually an absolute URI (e.g.
        // "https://evil.example.com/..."), .NET's Uri resolution silently replaces the base
        // authority with the attacker's host. Reject any result whose authority differs from
        // the configured upstream — the gateway must never forward credentials elsewhere.
        if (!string.Equals(resolved.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Resolved outbound URI authority '{resolved.Authority}' does not match " +
                $"the configured upstream '{baseUri.Authority}'. This request was blocked.");
        }

        return resolved;
    }
}
