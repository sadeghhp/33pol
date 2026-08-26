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

    /// <summary>
    /// Which scope the reported budget belongs to — <c>global</c>, <c>tenant</c>, <c>api_key</c>,
    /// <c>model</c>, <c>tenant_model</c> or <c>api_key_model</c>.
    /// </summary>
    /// <remarks>
    /// Several limits apply to one request, so a bare remaining-count is ambiguous: a client that
    /// sees 4 left cannot tell whether it is its own key, its whole organisation, or the model it
    /// chose that is nearly exhausted, and those call for three different responses. On a rejection
    /// this names the scope that refused; on a success, the one closest to refusing.
    /// </remarks>
    public const string RateLimitScope = "X-33pol-RateLimit-Scope";

    /// <summary>
    /// Present only while load-aware adaptation is holding the reported scope below its configured
    /// rate, as <c>&lt;effective&gt;/&lt;configured&gt;</c> requests per minute.
    /// </summary>
    /// <remarks>
    /// Absent means "you are being enforced exactly as configured", which is the answer to the first
    /// question anyone asks when a limit behaves unexpectedly. Emitting it unconditionally would make
    /// the interesting case invisible among the ordinary ones.
    /// </remarks>
    public const string RateLimitAdaptive = "X-33pol-RateLimit-Adaptive";
}
