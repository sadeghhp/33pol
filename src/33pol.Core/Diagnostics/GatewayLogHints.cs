using System.Net.Sockets;

namespace Pol33.Core.Diagnostics;

/// <summary>
/// Turns a failure into the one sentence an operator can act on. Every hint here names a concrete
/// next step; when the gateway cannot narrow the cause down to something actionable it returns
/// null rather than guessing, because a wrong hint costs more debugging time than no hint.
/// </summary>
public static class GatewayLogHints
{
    /// <summary>
    /// Explains an upstream HTTP failure for a model probe or proxied call.
    /// </summary>
    /// <param name="statusCode">Status the upstream returned.</param>
    /// <param name="upstreamUrl">The model's configured base URL, used to detect a duplicated path prefix.</param>
    /// <param name="endpointPath">Path the gateway appended, e.g. <c>/v1/chat/completions</c>.</param>
    /// <param name="modelId">Model id sent in the request body.</param>
    public static string? ForUpstreamStatus(
        int statusCode,
        string? upstreamUrl,
        string? endpointPath,
        string? modelId)
    {
        switch (statusCode)
        {
            case 404:
                return BuildNotFoundHint(upstreamUrl, endpointPath, modelId);

            case 401:
            case 403:
                return "The upstream rejected the credentials. Set or correct the model's API key in " +
                       "Routing → Models, or the environment variable its upstream auth points at.";

            case 400:
                return "The upstream rejected the request body. Check that the model's type in " +
                       "Routing → Models matches what the upstream actually serves — an embedding " +
                       "model probed as text generation fails this way.";

            case 408:
            case 504:
                return "The upstream did not answer in time. Check that it is loaded and not still " +
                       "warming up, and that no proxy between the gateway and it is timing out first.";

            case 429:
                return "The upstream is rate limiting the gateway. Reduce concurrency, or raise the " +
                       "limit on the upstream itself — the gateway's own rate limits do not apply here.";

            case 500:
            case 502:
            case 503:
                return "The upstream failed internally. Check its own logs; the gateway forwarded the " +
                       "request successfully and the error came back from the model server.";

            default:
                return null;
        }
    }

    /// <summary>
    /// A 404 from a model server is nearly always one of two things: the gateway built a path the
    /// server does not serve, or the server does not host the model id under that name.
    /// </summary>
    private static string BuildNotFoundHint(string? upstreamUrl, string? endpointPath, string? modelId)
    {
        if (HasDuplicatedPathPrefix(upstreamUrl, endpointPath, out var duplicated))
        {
            var trimmed = TrimTrailingSegment(upstreamUrl!, duplicated);
            return $"The model's URL already ends in '/{duplicated}', so the gateway called " +
                   $"'{CombinePreview(upstreamUrl, endpointPath)}' — a path the upstream does not serve. " +
                   $"Set the URL to the server root ('{trimmed}'); the gateway appends " +
                   $"'{endpointPath}' itself.";
        }

        var model = string.IsNullOrWhiteSpace(modelId) ? "the model" : $"'{modelId}'";
        return $"The upstream has no {endpointPath} route, or does not serve a model named {model}. " +
               "Check the model's URL points at the server root, and that the id matches the name the " +
               "upstream reports from GET /v1/models exactly (it is case-sensitive).";
    }

    /// <summary>
    /// True when the configured base URL already ends with the first segment of the path the gateway
    /// appends — the '/v1' + '/v1/chat/completions' double-prefix mistake.
    /// </summary>
    public static bool HasDuplicatedPathPrefix(string? upstreamUrl, string? endpointPath, out string duplicated)
    {
        duplicated = string.Empty;
        if (string.IsNullOrWhiteSpace(upstreamUrl) || string.IsNullOrWhiteSpace(endpointPath))
        {
            return false;
        }

        var firstSegment = endpointPath.Trim('/').Split('/', 2)[0];
        if (string.IsNullOrEmpty(firstSegment))
        {
            return false;
        }

        if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var basePath = uri.AbsolutePath.Trim('/');
        if (basePath.Length == 0)
        {
            return false;
        }

        var lastSegment = basePath[(basePath.LastIndexOf('/') + 1)..];
        if (!lastSegment.Equals(firstSegment, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        duplicated = lastSegment;
        return true;
    }

    /// <summary>Explains a transport-level failure — nothing answered, so there is no status code.</summary>
    public static string? ForException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused =>
                        "Nothing is listening on the model's URL. Check the upstream is running and the " +
                        "port is right. Running the gateway in Docker? 'localhost' means the container — " +
                        "use http://host.docker.internal:<port> instead.",
                    SocketError.HostNotFound or SocketError.NoData =>
                        "The model's hostname does not resolve. Check the spelling, and that the gateway's " +
                        "network (or Docker network) can see that host.",
                    SocketError.TimedOut =>
                        "The connection attempt timed out — the host is unreachable rather than refusing. " +
                        "Check firewall and network routing between the gateway and the upstream.",
                    _ => null,
                };
            }

            if (current is TaskCanceledException or TimeoutException)
            {
                return "The upstream did not respond before the gateway's timeout. Check that it is " +
                       "loaded and able to serve the request size being sent.";
            }
        }

        return null;
    }

    private static string CombinePreview(string? upstreamUrl, string? endpointPath)
    {
        var baseUrl = (upstreamUrl ?? string.Empty).TrimEnd('/');
        return baseUrl + "/" + (endpointPath ?? string.Empty).TrimStart('/');
    }

    private static string TrimTrailingSegment(string upstreamUrl, string segment)
    {
        var trimmed = upstreamUrl.TrimEnd('/');
        return trimmed.EndsWith('/' + segment, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^(segment.Length + 1)]
            : trimmed;
    }
}
