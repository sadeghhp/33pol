using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Middleware;

/// <summary>
/// Bounds uncredentialed traffic: refuses it before anything reads the body once its address is over
/// the anonymous tier, and charges it for the requests the security layer turns away.
/// </summary>
/// <remarks>
/// <para><see cref="PublicModelDetectionMiddleware"/> has to run ahead of authentication — it is what
/// tells authentication a request may be served without a key — and to do its job it buffers and
/// JSON-parses the body. So while any model is <c>publicAccess</c>, every inference POST was
/// buffered and parsed before a single limiter had looked at it: the cheapest thing a limiter does,
/// saying no, happened after the most expensive thing the gateway does on a refused request.</para>
///
/// <para>This closes that for the traffic it matters for. A request carrying no credential can only
/// ever be metered against the anonymous tier for its address, and that tier's bucket is already
/// known before the body is read — so an address over it is answered here, with no buffering, no
/// parse, and no work for the rest of the pipeline. A credentialed request is left alone: its tier
/// depends on the tenant its key resolves to, which nothing knows yet, and
/// <see cref="AuthFailureRateLimitMiddleware"/> is what bounds it.</para>
///
/// <para><b>It peeks on the way in, and charges only what nothing else will.</b> An uncredentialed
/// request that is served is charged by <see cref="RateLimitMiddleware"/> against this very
/// partition, so charging it here too would bill it twice. One the security layer refuses never
/// reaches that middleware at all — the limiter proper sits behind security — and used to be free:
/// an uncredentialed caller could take a `401` from the admin API, or from inference with no public
/// model registered, as fast as the network allowed. Those are charged here, on the same partition
/// and the same tier, so uncredentialed traffic is bounded whether it is served or refused.</para>
/// </remarks>
public sealed class AnonymousAdmissionGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimitPolicyResolver _policyResolver;
    private readonly IDistributedRateLimitStore _rateLimitStore;
    private readonly IErrorResponseWriter _errors;
    private readonly IGatewayMetricsCollector _metrics;
    private readonly IAdaptiveRateLimitGovernor? _governor;
    private readonly TimeProvider _timeProvider;

    public AnonymousAdmissionGuardMiddleware(
        RequestDelegate next,
        IRateLimitPolicyResolver policyResolver,
        IDistributedRateLimitStore rateLimitStore,
        IErrorResponseWriter errors,
        IGatewayMetricsCollector metrics,
        IAdaptiveRateLimitGovernor? governor = null,
        TimeProvider? timeProvider = null)
    {
        _next = next;
        _policyResolver = policyResolver;
        _rateLimitStore = rateLimitStore;
        _errors = errors;
        _metrics = metrics;
        _governor = governor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsGuardedPath(context) || PresentsCredential(context) || !_policyResolver.IsEnabled())
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // The tier the plan resolver will apply to this same partition: the anonymous one where the
        // gateway requires credentials and an anonymous tier is configured, the default otherwise.
        // Resolved through the same helper, so the guard can never refuse against a budget the
        // limiter would not have enforced.
        var policy = _policyResolver.ResolveAnonymous();

        // Two names for the same caller, and they are not interchangeable. The store is keyed by the
        // tenant scope's bucket name; the governor and the metrics are keyed by the bare partition,
        // which is what RateLimitMiddleware and the router use. Reporting the bucket name to those
        // two would give this caller a second identity: an escalation the router's admitted signal
        // could never clear, and a duplicate row in the console's rejections-by-tenant list.
        var partition = RateLimitPartition.Resolve(context);
        var bucketKey = RateLimitKeys.Tenant(partition);
        var now = _timeProvider.GetUtcNow();

        var budget = _rateLimitStore.PeekRequest(bucketKey, policy, now);
        if (budget.IsAcquired)
        {
            await _next(context).ConfigureAwait(false);

            if (WasCredentialRejected(context))
            {
                // Refused by the security layer, so the limiter proper never saw it. Charged against
                // the clock the decision was made on, exactly as the auth-failure budget charges its
                // own refusals.
                _rateLimitStore.DebitRequest(bucketKey, policy, now);
            }

            return;
        }

        // Reported exactly as the limiter proper reports a tenant-scope refusal: the same partition
        // and the same budget refused it, only earlier, and a console that showed these under a
        // separate reason would split one limit across two rows. The scope is stamped on here
        // because a peek does not carry one — without it the answer would be the only rate refusal
        // in the gateway with no X-33pol-RateLimit-Scope header.
        RateLimitResponseHeaders.Write(context, budget with { Scope = RateLimitScope.Tenant }, refused: true);
        _metrics.RecordRateLimitRejection("rate_limit:tenant", partition, modelId: null);

        var retryAfter = budget.RetryAfterSeconds ?? 1;
        if (_governor is not null)
        {
            _governor.RecordOutcome(partition, admitted: false, now);
            retryAfter = _governor.GetRetryAfterSeconds(partition, retryAfter, now);
        }

        await context.WriteGatewayErrorAsync(
            _errors.Write(GatewayErrorCode.RateLimitExceeded),
            context.RequestAborted,
            retryAfter).ConfigureAwait(false);
    }

    private static bool WasCredentialRejected(HttpContext context) =>
        context.Items.TryGetValue(GatewayAuthContextItems.CredentialRejected, out var value) && value is true;

    /// <summary>
    /// Inference and the admin API: the paths an uncredentialed caller can reach and be refused on.
    /// The model listing is deliberately absent — an uncredentialed <c>GET /v1/models</c> is served,
    /// not refused, and <see cref="RateLimitMiddleware"/> meters it like any other served request.
    /// </summary>
    private static bool IsGuardedPath(HttpContext context) =>
        InferenceRouteClassifier.IsRoutableInference(context) ||
        context.Request.Path.StartsWithSegments(AdminApiPrefix, StringComparison.OrdinalIgnoreCase);

    private const string AdminApiPrefix = "/admin/api";

    /// <summary>
    /// Whether the request offers a credential at all. Read through the same helper the security
    /// layer authenticates with, so a request this guard treats as anonymous is exactly one the
    /// limiter will meter against the anonymous tier.
    /// </summary>
    private static bool PresentsCredential(HttpContext context) =>
        GatewayCredential.IsPresent(
            context.Request.Headers[GatewayCredential.ApiKeyHeader].ToString(),
            context.Request.Headers.Authorization.ToString());
}
