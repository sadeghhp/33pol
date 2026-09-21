using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.RateLimiting;
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
/// <para><b>Only requests that present a credential.</b> A caller offering no key is not guessing at
/// one: it is either an anonymous request to a public model, which <see cref="RateLimitMiddleware"/>
/// meters against the anonymous tier, or a request the security layer refuses for free. Charging
/// those made anonymous traffic spend a budget it could never be admitted by, and refusing them made
/// one client's stale key a lockout for every anonymous caller sharing its address.</para>
///
/// <para>This wraps the security middleware instead of preceding it, so it can charge for the
/// outcome rather than the attempt. Every credentialed request peeks at the budget for its address
/// on the way in; on the way out, only the ones the security layer refused with a <c>401</c> are
/// charged a token. That refusal is read from
/// <see cref="GatewayAuthContextItems.CredentialRejected"/>, which only the security layer sets,
/// never from the status code: a <c>403</c> the router writes for an ungranted model, or a
/// <c>401</c> copied from an upstream provider, is not a guessed credential and must not spend this
/// budget. Traffic that authenticates passes through untouched and is metered by
/// <see cref="RateLimitMiddleware"/> against its tenant — the two budgets are separate and neither
/// can exhaust the other.</para>
///
/// <para><b>Past the budget, in two stages.</b> An address that has spent its budget is refused
/// unless it can prove a credential; the limiter runs the gateway's authentication scheme itself at
/// that point, and the framework caches the result so the security middleware pays for no second
/// lookup. Refusing everything from the address instead — a good key included — made a shared
/// address a lockout: behind an ingress without <c>ForwardedHeaders</c>, or a corporate NAT, one
/// client with a stale key refused once a second held every other caller, and the operator's admin
/// access, at 429 for as long as it kept going.</para>
///
/// <para>Proving a credential is not free, though. An unknown key misses the validator's negative
/// cache and costs a database read, so an attacker rotating random keys past the budget would keep
/// paying the gateway's most expensive part of a refusal while the limiter only shortened the
/// answer. The proof itself is therefore budgeted, against the same address at
/// <see cref="RateLimitingOptions.AuthFailureProbeMultiplier"/> times the rate: within it a real key
/// still gets through, and past it the request is refused with nothing validated at all.</para>
///
/// <para>The partition is the client address and nothing else. There is no identity to key on before
/// authentication has run, and keying on the offered credential would let an attacker mint a fresh
/// budget for every guess. The address has to be the caller's for the budget to be fair, which is
/// what <c>ForwardedHeaders</c> decides — behind an ingress that is not configured for it, every
/// anonymous caller shares one budget.</para>
///
/// <para>Governed by <see cref="RateLimitingOptions.AuthFailureProtectionEnabled"/> rather than by
/// the rate-limiting master switch: the switch is how an operator stops shaping client traffic
/// during an incident, and that action must not also switch off a security control.</para>
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
    private readonly IRateLimitUsageTracker? _usage;
    private readonly bool _enabled;
    private readonly int _probeMultiplier;

    public AuthFailureRateLimitMiddleware(
        RequestDelegate next,
        IRateLimitPolicyResolver policyResolver,
        IDistributedRateLimitStore rateLimitStore,
        IErrorResponseWriter errors,
        IGatewayMetricsCollector metrics,
        TimeProvider? timeProvider = null,
        IOptions<RateLimitingOptions>? options = null,
        IRateLimitUsageTracker? usage = null)
    {
        _usage = usage;
        _next = next;
        _policyResolver = policyResolver;
        _rateLimitStore = rateLimitStore;
        _errors = errors;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _enabled = options?.Value.AuthFailureProtectionEnabled ?? true;
        _probeMultiplier = Math.Clamp(options?.Value.AuthFailureProbeMultiplier ?? 10, 1, 1_000);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled || !IsCredentialGuardedPath(context) || !PresentsCredential(context))
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
        if (!budget.IsAcquired && !await ProvesCredentialWithinProbeBudgetAsync(context, partitionKey, policy, now)
                .ConfigureAwait(false))
        {
            _metrics.RecordRateLimitRejection("auth_failure", partitionKey, modelId: null);
            _usage?.RecordAuthFailure(RateLimitAuthFailureStep.Refused, policy.Rpm);
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.RateLimitExceeded),
                context.RequestAborted,
                budget.RetryAfterSeconds).ConfigureAwait(false);
            return;
        }

        _usage?.RecordAuthFailure(RateLimitAuthFailureStep.Checked, policy.Rpm);

        await _next(context).ConfigureAwait(false);

        if (WasCredentialRejected(context))
        {
            _usage?.RecordAuthFailure(RateLimitAuthFailureStep.Charged, policy.Rpm);
            // Charged after the fact, against the clock the decision was made on.
            _rateLimitStore.DebitRequest(partitionKey, policy, now);
        }
    }

    /// <summary>
    /// Whether this request may be admitted despite the address being over its auth-failure budget:
    /// there is validation left in the address's probe budget, and the credential authenticates.
    /// </summary>
    /// <remarks>
    /// <para>The token is taken before the lookup, because the cost is incurred whatever the
    /// credential turns out to be, and handed back when the credential authenticates. That is what
    /// keeps the allowance a bound on <em>wasted</em> work rather than on traffic: a real key is
    /// answered from the validator's positive cache, so proving it again costs nothing and must not
    /// count against the address. Only failures — the lookups that reach the database — hold a
    /// token, and once they have spent the allowance nothing is validated at all: an attacker past
    /// both budgets is refused on two dictionary lookups.</para>
    /// </remarks>
    private async Task<bool> ProvesCredentialWithinProbeBudgetAsync(
        HttpContext context,
        string partitionKey,
        RateLimitPolicy policy,
        DateTimeOffset now)
    {
        var probeKey = RateLimitKeys.AuthProbe(partitionKey);
        var probePolicy = Widen(policy, _probeMultiplier);

        if (!_rateLimitStore.TryAcquireRequest(probeKey, probePolicy, now).IsAcquired)
        {
            return false;
        }

        if (!await ProvesCredentialAsync(context).ConfigureAwait(false))
        {
            return false;
        }

        _rateLimitStore.RefundRequest(probeKey, probePolicy, now);
        return true;
    }

    /// <summary>
    /// The auth-failure tier scaled up by <paramref name="multiplier"/>, saturating rather than
    /// overflowing so a large configured rate cannot wrap into a negative capacity.
    /// </summary>
    private static RateLimitPolicy Widen(RateLimitPolicy policy, int multiplier) =>
        new(
            Multiply(policy.Rpm, multiplier),
            Multiply(policy.Burst, multiplier),
            MaxConcurrentStreams: 0);

    /// <remarks>
    /// Saturates at half of <see cref="int.MaxValue"/> rather than at the whole of it, because the
    /// two products are summed: <see cref="RateLimitPolicy.Capacity"/> is <c>Rpm + Burst</c>, and
    /// two saturated halves still add up to a positive capacity. Saturating at the maximum would
    /// have that sum overflow to a negative number, which the store reads as "this control is off" —
    /// turning an absurd configuration into no allowance limit at all.
    /// </remarks>
    private static int Multiply(int value, int multiplier)
    {
        const int Ceiling = int.MaxValue / 2;
        return value >= Ceiling / multiplier ? Ceiling : value * multiplier;
    }

    /// <summary>
    /// Whether the request carries a credential that authenticates, decided now rather than one
    /// middleware later.
    /// </summary>
    /// <remarks>
    /// Runs the gateway's own scheme through the framework's authentication service. The handler and
    /// its result are cached per request by the framework, so the security middleware's own call
    /// reuses them. A pipeline with no authentication service or scheme (hand-built tests, a host
    /// without security) can vouch for no one and is refused.
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
    /// Whether the request offers a credential at all. Read through the same helper the security
    /// layer authenticates with, so the two cannot disagree about which requests are credentialed.
    /// </summary>
    private static bool PresentsCredential(HttpContext context) =>
        GatewayCredential.IsPresent(
            context.Request.Headers[GatewayCredential.ApiKeyHeader].ToString(),
            context.Request.Headers.Authorization.ToString());

    /// <summary>
    /// The paths a credential is checked on: inference, and the admin API. Anything else either
    /// carries no credential or is already anonymous by design.
    /// </summary>
    private static bool IsCredentialGuardedPath(HttpContext context) =>
        InferenceRouteClassifier.IsRoutableInference(context) ||
        context.Request.Path.StartsWithSegments(AdminApiPrefix, StringComparison.OrdinalIgnoreCase);
}
