using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Middleware;

/// <summary>
/// Bounds how fast one client address may be refused by authentication.
/// </summary>
/// <remarks>
/// <para><see cref="RateLimitMiddleware"/> sits behind the security middleware, because it needs the
/// tenant a key resolves to before it can pick a tier. Everything the security middleware turns away
/// therefore never reaches a rate limiter at all: a caller could offer a wrong key — or no key — to
/// an inference or admin path as fast as it liked, and the only ceiling was the network's.</para>
///
/// <para>This wraps the security middleware instead of preceding it, so it can charge for the
/// outcome rather than the attempt. Every request peeks at the budget for its address on the way in
/// and is refused once that budget is empty; on the way out, only the ones answered <c>401</c> or
/// <c>403</c> are charged a token. Traffic that authenticates successfully passes through untouched
/// and is metered by <see cref="RateLimitMiddleware"/> against its tenant, as before — the two
/// budgets are separate and neither can exhaust the other.</para>
///
/// <para>The partition is the client address and nothing else. There is no identity to key on before
/// authentication has run, and keying on the offered credential would let an attacker mint a fresh
/// budget for every guess.</para>
///
/// <para>That is also the blast radius, and it is deliberate: once an address has spent its budget,
/// the next request from it is refused before authentication, whether or not it carries a good key.
/// The address has to be the caller's for that to be fair, which is what <c>ForwardedHeaders</c>
/// decides — behind an ingress that is not configured for it, every caller shares one address and
/// therefore one budget. The default tier is the allowance, so reaching it means thousands of
/// rejected credentials in a minute from one address; a deployment that cannot distinguish its
/// callers should raise the default tier or configure the trusted proxy.</para>
/// </remarks>
public sealed class AuthFailureRateLimitMiddleware
{
    private const string AdminApiPrefix = "/admin/api";

    private readonly RequestDelegate _next;
    private readonly IRateLimitPolicyResolver _policyResolver;
    private readonly IDistributedRateLimitStore _rateLimitStore;
    private readonly IErrorResponseWriter _errors;
    private readonly IGatewayMetricsCollector _metrics;
    private readonly TimeProvider _timeProvider;

    public AuthFailureRateLimitMiddleware(
        RequestDelegate next,
        IRateLimitPolicyResolver policyResolver,
        IDistributedRateLimitStore rateLimitStore,
        IErrorResponseWriter errors,
        IGatewayMetricsCollector metrics,
        TimeProvider? timeProvider = null)
    {
        _next = next;
        _policyResolver = policyResolver;
        _rateLimitStore = rateLimitStore;
        _errors = errors;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsCredentialGuardedPath(context) || !_policyResolver.IsEnabled())
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var partitionKey = RateLimitPartition.ResolveAuthFailure(context);

        // The default tier: there is no tenant yet, so no plan or per-tenant override can apply.
        var policy = _policyResolver.Resolve(planSlug: null, tenantSlug: null);
        var now = _timeProvider.GetUtcNow();

        var budget = _rateLimitStore.PeekRequest(partitionKey, policy, now);
        if (!budget.IsAcquired)
        {
            _metrics.RecordRateLimitRejection("auth_failure", partitionKey, modelId: null);
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.RateLimitExceeded),
                context.RequestAborted,
                budget.RetryAfterSeconds).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);

        if (IsCredentialRejection(context.Response.StatusCode))
        {
            // Charged after the fact, against the clock the decision was made on.
            _rateLimitStore.DebitRequest(partitionKey, policy, now);
        }
    }

    /// <summary>
    /// The paths a credential is checked on: inference, and the admin API. Anything else either
    /// carries no credential or is already anonymous by design.
    /// </summary>
    private static bool IsCredentialGuardedPath(HttpContext context) =>
        InferenceRouteClassifier.IsRoutableInference(context) ||
        context.Request.Path.StartsWithSegments(AdminApiPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsCredentialRejection(int statusCode) =>
        statusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden;
}
