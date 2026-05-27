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
        var baseUri = ToForwarderDestination(destinationPrefix);
        var relative = path.HasValue ? path.Value!.TrimStart('/') : string.Empty;
        if (query.HasValue)
        {
            relative += query.Value;
        }

        return new Uri(new Uri(baseUri, UriKind.Absolute), relative);
    }
}
