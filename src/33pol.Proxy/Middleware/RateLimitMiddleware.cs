using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.RateLimiting;
using Pol33.Proxy.Errors;
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

        // Global master switch, read from the live snapshot so the admin toggle applies without a restart.
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
        var partitionKey = tenantContext?.TenantId ?? "anonymous";

        var policy = _policyResolver.Resolve(planSlug, tenantSlug);
        var now = _timeProvider.GetUtcNow();
        var acquire = _rateLimitStore.TryAcquireRequest(partitionKey, policy, now);
        if (!acquire.IsAcquired)
        {
            _metrics.RecordRateLimitRejection(acquire.RejectionReason?.ToString() ?? "rate_limit");
            await WriteRateLimitErrorAsync(context, acquire).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
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
