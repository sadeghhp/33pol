using Microsoft.AspNetCore.Http;
using Pol33.Core.Errors;
using Pol33.Core.RateLimiting;

namespace Pol33.Proxy.Routing;

/// <summary>
/// Publishes a partition's remaining budget on the response, so a client can pace itself instead of
/// discovering the limit by being refused.
/// </summary>
/// <remarks>
/// Shared by every place that answers a rate-limit decision — the limiter proper, the pre-parse
/// anonymous guard, and the router's stream-concurrency cap. Each used to write its own headers, or
/// in the router's case none at all, so whether a client learned anything about the limit that
/// refused it depended on which control had refused it.
/// </remarks>
public static class RateLimitResponseHeaders
{
    /// <param name="refused">
    /// Whether this is a response the gateway is refusing. On a refusal the standard
    /// <c>RateLimit-*</c> names are written alongside the vendor-prefixed ones: nothing from an
    /// upstream can overwrite them there, and a refusal is the response a client most needs to be
    /// able to read without knowing the gateway's own header names.
    /// </param>
    public static void Write(HttpContext context, RateLimitAcquireResult acquire, bool refused = false)
    {
        if (acquire.Limit is not { } limit)
        {
            return;
        }

        var headers = context.Response.Headers;
        var remaining = (acquire.Remaining ?? 0).ToString();
        var reset = (acquire.ResetAfterSeconds ?? 0).ToString();

        headers[GatewayHeaders.RateLimitLimit] = limit.ToString();
        headers[GatewayHeaders.RateLimitRemaining] = remaining;
        headers[GatewayHeaders.RateLimitReset] = reset;

        if (refused)
        {
            headers[GatewayHeaders.StandardRateLimitLimit] = limit.ToString();
            headers[GatewayHeaders.StandardRateLimitRemaining] = remaining;
            headers[GatewayHeaders.StandardRateLimitReset] = reset;
        }

        if (acquire.Scope is { } scope)
        {
            // Several limits apply at once, so a bare remaining-count is ambiguous: this says which
            // of them the number belongs to.
            headers[GatewayHeaders.RateLimitScope] = scope.ToLabel();
        }

        if (acquire.AdaptiveFactor < 1.0 && acquire.ConfiguredRpm > 0)
        {
            // The two rates the governor moved between, read straight off the rule rather than
            // reconstructed from the capacity: Scale() rounds rpm and burst independently, so
            // dividing the capacity by the factor does not invert it and the header was off by a
            // few whenever either rounding went the other way.
            headers[GatewayHeaders.RateLimitAdaptive] = $"{acquire.EffectiveRpm}/{acquire.ConfiguredRpm}";
        }
    }
}
