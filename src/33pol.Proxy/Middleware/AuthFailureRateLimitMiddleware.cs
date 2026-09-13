using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Security;
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
/// outcome rather than the attempt. Every request peeks at the budget for its address on the way in;
/// on the way out, only the ones the security layer refused with a <c>401</c> are charged a token.
/// That refusal is read from <see cref="GatewayAuthContextItems.CredentialRejected"/>, which only the
/// security layer sets, never from the status code: a <c>403</c> the router writes for an ungranted
/// model, or a <c>401</c> copied from an upstream provider, is not a guessed credential and must not
/// spend this budget. Traffic that authenticates passes through untouched and is metered by
/// <see cref="RateLimitMiddleware"/> against its tenant — the two budgets are separate and neither
/// can exhaust the other.</para>
///
/// <para>Once an address has spent its budget, only requests that cannot prove a credential are
/// refused. The limiter runs the gateway's authentication scheme itself at that point; a key that
/// validates passes, and the framework caches the result so the security middleware pays for no
/// second lookup. Refusing everything from the address instead — a good key included — made a shared
/// address a lockout: behind an ingress without <c>ForwardedHeaders</c>, or a corporate NAT, one
/// client with a stale key refused once a second held every other caller, and the operator's admin
/// access, at 429 for as long as it kept going.</para>
///
/// <para>The partition is the client address and nothing else. There is no identity to key on before
/// authentication has run, and keying on the offered credential would let an attacker mint a fresh
/// budget for every guess. The address has to be the caller's for the budget to be fair, which is
/// what <c>ForwardedHeaders</c> decides — behind an ingress that is not configured for it, every
/// anonymous caller shares one budget.</para>
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

        // The auth-failure tier: there is no tenant yet, so no plan or per-tenant override can
        // apply, and a tier sized for legitimate traffic is far too generous for credential
        // guessing. Falls back to the default tier where none is configured.
        var policy = _policyResolver.ResolveAuthFailure();
        var now = _timeProvider.GetUtcNow();

        var budget = _rateLimitStore.PeekRequest(partitionKey, policy, now);
        if (!budget.IsAcquired && !await ProvesCredentialAsync(context).ConfigureAwait(false))
        {
            _metrics.RecordRateLimitRejection("auth_failure", partitionKey, modelId: null);
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.RateLimitExceeded),
                context.RequestAborted,
                budget.RetryAfterSeconds).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);

        if (WasCredentialRejected(context))
        {
            // Charged after the fact, against the clock the decision was made on.
            _rateLimitStore.DebitRequest(partitionKey, policy, now);
        }
    }

    /// <summary>
    /// Whether the request carries a credential that authenticates, decided now rather than one
    /// middleware later.
    /// </summary>
    /// <remarks>
    /// Runs the gateway's own scheme through the framework's authentication service. The handler and
    /// its result are cached per request by the framework, so the security middleware's own call
    /// reuses them and a refused request on an exhausted address costs one peek and one cached key
    /// lookup. A pipeline with no authentication service or scheme (hand-built tests, a host without
    /// security) can vouch for no one and is refused.
    /// </remarks>
    private static async Task<bool> ProvesCredentialAsync(HttpContext context)
    {
        var authentication = context.RequestServices?.GetService<IAuthenticationService>();
        if (authentication is null)
        {
            return false;
        }

        try
        {
            var result = await authentication
                .AuthenticateAsync(context, GatewayAuthSchemes.ApiKey)
                .ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (InvalidOperationException)
        {
            // The scheme is not registered in this host.
            return false;
        }
    }

    private static bool WasCredentialRejected(HttpContext context) =>
        context.Items.TryGetValue(GatewayAuthContextItems.CredentialRejected, out var value) && value is true;

    /// <summary>
    /// The paths a credential is checked on: inference, and the admin API. Anything else either
    /// carries no credential or is already anonymous by design.
    /// </summary>
    private static bool IsCredentialGuardedPath(HttpContext context) =>
        InferenceRouteClassifier.IsRoutableInference(context) ||
        context.Request.Path.StartsWithSegments(AdminApiPrefix, StringComparison.OrdinalIgnoreCase);
}
