using System.Net;

namespace Pol33.Api.Services;

public sealed class ProviderModelsUpstreamException(HttpStatusCode statusCode) : Exception(
    $"Upstream model list returned HTTP {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
