namespace Pol33.Core.Errors;

/// <summary>
/// <c>HttpContext.Items</c> keys used to carry error-capture context between middleware, without
/// the proxy having to reference the observability layer.
/// </summary>
public static class GatewayErrorContextKeys
{
    /// <summary>Redacted upstream error body, stashed by the response transformer.</summary>
    public const string UpstreamBodySnippet = "GatewayUpstreamBodySnippet";

    /// <summary>Sanitized upstream base URL the request was forwarded to.</summary>
    public const string UpstreamTarget = "GatewayUpstreamTarget";

    /// <summary>
    /// The transport-level exception behind a forwarder outcome, stashed by the forwarder so the
    /// error record carries the real cause ("connection refused") and not only the outcome name.
    /// </summary>
    public const string UpstreamException = "GatewayUpstreamException";

    /// <summary>
    /// Set once a capture point has recorded this request's failure, so the terminal exception
    /// handler does not record it a second time under a different source.
    /// </summary>
    public const string ErrorCaptured = "GatewayErrorCaptured";
}
