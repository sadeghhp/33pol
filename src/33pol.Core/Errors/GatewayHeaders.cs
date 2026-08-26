namespace Pol33.Core.Errors;

public static class GatewayHeaders
{
    public const string RequestId = "X-Request-Id";
    public const string ErrorCode = "X-33pol-Error-Code";
    public const string RetryAfter = "Retry-After";

    public const string QuotaWarning = "X-33pol-Quota-Warning";

    /// <summary>The partition's request budget (bucket capacity: <c>rpm + burst</c>).</summary>
    /// <remarks>
    /// Vendor-prefixed rather than the conventional <c>X-RateLimit-*</c> on purpose: the response
    /// headers the upstream provider returns are copied onto the response after this middleware has
    /// run, so an unprefixed name would be silently overwritten by the provider's own budget — a
    /// number about a different limit entirely. The prefix also matches the rest of the gateway's
    /// headers.
    /// </remarks>
    public const string RateLimitLimit = "X-33pol-RateLimit-Limit";

    /// <summary>Whole requests left in the partition's budget after the current request.</summary>
    public const string RateLimitRemaining = "X-33pol-RateLimit-Remaining";

    /// <summary>Seconds until the partition is back at its full budget.</summary>
    public const string RateLimitReset = "X-33pol-RateLimit-Reset";
}
