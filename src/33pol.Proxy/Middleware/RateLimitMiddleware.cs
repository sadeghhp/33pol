using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.RateLimiting;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Parsing;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Middleware;

public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimitPolicyResolver _policyResolver;
    private readonly IDistributedRateLimitStore _rateLimitStore;
    private readonly IErrorResponseWriter _errors;
    private readonly IGatewayMetricsCollector _metrics;
    private readonly TimeProvider _timeProvider;

    public RateLimitMiddleware(
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
        if (!InferenceRouteClassifier.IsRoutableInference(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Global master switch, read from the live snapshot so the admin toggle applies without a
        // restart. With it off, nothing below runs — including the early answer for unroutable
        // bodies, which the router gives identically a few frames later.
        if (!_policyResolver.IsEnabled())
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var tenantContext = context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var value)
            ? value as TenantContext
            : null;

        var planSlug = tenantContext?.PlanSlug;
        var tenantSlug = tenantContext?.TenantId;
        var partitionKey = RateLimitPartition.Resolve(context);

        var policy = _policyResolver.Resolve(planSlug, tenantSlug);
        var now = _timeProvider.GetUtcNow();
        var acquire = _rateLimitStore.TryAcquireRequest(partitionKey, policy, now);
        WriteBudgetHeaders(context, acquire);

        if (!acquire.IsAcquired)
        {
            _metrics.RecordRateLimitRejection(acquire.RejectionReason?.ToString() ?? "rate_limit", partitionKey, modelId: null);
            await WriteRateLimitErrorAsync(context, acquire).ConfigureAwait(false);
            return;
        }

        // Answered here, after the debit, when the cached parse already says the router is going to
        // refuse this body: it saves the rest of the pipeline for a request that cannot be served.
        // The debit has to come first — answering ahead of it made an unroutable body a free request,
        // so a tenant could send malformed 25 MB payloads at any rate it liked and never be limited.
        if (await RejectUnroutableBodyAsync(context).ConfigureAwait(false))
        {
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the same <c>invalid_json</c> / <c>missing_model</c> answer the router would, when the
    /// cached parse already says the body cannot be routed. Returns false when the body is routable
    /// or nothing is cached yet.
    /// </summary>
    /// <remarks>
    /// The parse is cached only when <c>PublicModelDetectionMiddleware</c> ran a parse of its own,
    /// which it does only while at least one model is public. With none, nothing is cached and the
    /// router answers instead — same status, same body, one middleware later.
    /// </remarks>
    private async Task<bool> RejectUnroutableBodyAsync(HttpContext context)
    {
        if (!InferenceRequestParseCache.TryGet(context, out var cached))
        {
            return false;
        }

        if (cached is null)
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.InvalidJson),
                context.RequestAborted).ConfigureAwait(false);
            return true;
        }

        if (string.IsNullOrWhiteSpace(cached.Value.Model))
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.MissingModel),
                context.RequestAborted).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Publishes the partition's remaining budget on every answer, admitted or not, so a client can
    /// pace itself instead of discovering the limit by being refused.
    /// </summary>
    private static void WriteBudgetHeaders(HttpContext context, RateLimitAcquireResult acquire)
    {
        if (acquire.Limit is not { } limit)
        {
            return;
        }

        context.Response.Headers[GatewayHeaders.RateLimitLimit] = limit.ToString();
        context.Response.Headers[GatewayHeaders.RateLimitRemaining] = (acquire.Remaining ?? 0).ToString();
        context.Response.Headers[GatewayHeaders.RateLimitReset] = (acquire.ResetAfterSeconds ?? 0).ToString();
    }

    private Task WriteRateLimitErrorAsync(HttpContext context, RateLimitAcquireResult acquire)
    {
        var code = acquire.RejectionReason switch
        {
            GatewayRateLimitReason.ConcurrencyLimitExceeded => GatewayErrorCode.ConcurrencyLimitExceeded,
            _ => GatewayErrorCode.RateLimitExceeded,
        };

        return context.WriteGatewayErrorAsync(
            _errors.Write(code),
            context.RequestAborted,
            acquire.RetryAfterSeconds);
    }
}
