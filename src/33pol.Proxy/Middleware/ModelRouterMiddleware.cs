using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Models;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.RateLimiting;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Forwarding;
using Pol33.Proxy.Parsing;
using Pol33.Proxy.Resilience;
using Pol33.Proxy.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Middleware;

public sealed class ModelRouterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IModelRegistry _registry;
    private readonly IBackendHealthStore _healthStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGatewayAuthenticationState _authState;
    private readonly IErrorResponseWriter _errors;
    private readonly IRequestTracker _requestTracker;
    private readonly IRecentRequestStore _recentRequestStore;
    private readonly ModelCircuitBreakerRegistry _circuitBreakers;
    private readonly BulkheadRegistry _bulkhead;
    private readonly IRateLimitPolicyResolver _rateLimitPolicyResolver;
    private readonly IDistributedRateLimitStore _rateLimitStore;
    private readonly IHttpForwarder _forwarder;
    private readonly HttpMessageInvoker _httpClient;
    private readonly TimeSpan _forwardTimeout;
    private readonly ILogger<ModelRouterMiddleware> _logger;

    public ModelRouterMiddleware(
        RequestDelegate next,
        IModelRegistry registry,
        IBackendHealthStore healthStore,
        IServiceScopeFactory scopeFactory,
        IGatewayAuthenticationState authState,
        IErrorResponseWriter errors,
        IRequestTracker requestTracker,
        IRecentRequestStore recentRequestStore,
        ModelCircuitBreakerRegistry circuitBreakers,
        BulkheadRegistry bulkhead,
        IRateLimitPolicyResolver rateLimitPolicyResolver,
        IDistributedRateLimitStore rateLimitStore,
        IHttpForwarder forwarder,
        HttpMessageInvoker httpClient,
        IOptions<GatewayOptions> options,
        ILogger<ModelRouterMiddleware> logger)
    {
        _next = next;
        _registry = registry;
        _healthStore = healthStore;
        _scopeFactory = scopeFactory;
        _authState = authState;
        _errors = errors;
        _requestTracker = requestTracker;
        _recentRequestStore = recentRequestStore;
        _circuitBreakers = circuitBreakers;
        _bulkhead = bulkhead;
        _rateLimitPolicyResolver = rateLimitPolicyResolver;
        _rateLimitStore = rateLimitStore;
        _forwarder = forwarder;
        _httpClient = httpClient;
        _forwardTimeout = TimeSpan.FromSeconds(options.Value.Resilience.ForwardTimeoutSeconds);
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (InferenceRouteClassifier.IsPassthroughPath(context.Request.Path) ||
            !InferenceRouteClassifier.IsRoutableInference(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();

        InferenceRequestInfo requestInfo;
        try
        {
            requestInfo = await InferenceRequestParser.ParseAsync(context.Request.Body, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.InvalidJson),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(requestInfo.Model))
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.MissingModel),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (!_registry.TryGetModel(requestInfo.Model, out var modelConfig) || modelConfig is null)
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(
                    GatewayErrorCode.ModelNotFound,
                    message: $"Model '{requestInfo.Model}' not found."),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (_authState.IsAuthenticationRequired &&
            context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var tenantValue) &&
            tenantValue is TenantContext tenantContext &&
            Guid.TryParse(tenantContext.TenantId, out var tenantId))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var modelGrants = scope.ServiceProvider.GetRequiredService<IModelGrantService>();
            if (!await modelGrants.IsModelAllowedAsync(tenantId, modelConfig.Id, context.RequestAborted).ConfigureAwait(false))
        {
                await context.WriteGatewayErrorAsync(
                    _errors.Write(
                        GatewayErrorCode.InsufficientScope,
                        message: "The API key is not granted access to this model."),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }
        }

        if (!_healthStore.IsBackendHealthy(modelConfig.Id))
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(
                    GatewayErrorCode.BackendUnhealthy,
                    message: $"Backend for model '{modelConfig.Id}' is unhealthy."),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (!_circuitBreakers.TryEnter(modelConfig.Id))
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.CircuitOpen),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var bulkheadLease = await _bulkhead.TryAcquireAsync(modelConfig.Id, context.RequestAborted).ConfigureAwait(false);
        if (bulkheadLease is null)
        {
            _circuitBreakers.RecordFailure(modelConfig.Id);
            await context.WriteGatewayErrorAsync(
                _errors.Write(
                    GatewayErrorCode.UpstreamError,
                    message: "Too many concurrent requests for this model."),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        using (bulkheadLease)
        {
            var ratePartitionKey = ResolveRateLimitPartitionKey(context);
            var planSlug = context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var rateTenantItem) &&
                           rateTenantItem is TenantContext rateTenantContext
                ? rateTenantContext.PlanSlug
                : null;
            var ratePolicy = _rateLimitPolicyResolver.Resolve(planSlug, ratePartitionKey);

            var streamSlotAcquired = false;
            if (requestInfo.Stream)
            {
                var streamAcquire = _rateLimitStore.TryAcquireStreamSlot(ratePartitionKey, ratePolicy);
                if (!streamAcquire.IsAcquired)
                {
                    _circuitBreakers.RecordFailure(modelConfig.Id);
                    await context.WriteGatewayErrorAsync(
                        _errors.Write(GatewayErrorCode.ConcurrencyLimitExceeded),
                        context.RequestAborted,
                        streamAcquire.RetryAfterSeconds).ConfigureAwait(false);
                    return;
                }

                streamSlotAcquired = true;
            }

            try
            {
                context.Request.Body.Position = 0;

            var started = DateTimeOffset.UtcNow;
            using var _ = _requestTracker.BeginInferenceRequest(modelConfig.Id, requestInfo.Stream);

            var transformer = new StreamingHttpTransformer(
                requestInfo.Stream,
                requestInfo.Model,
                modelConfig.Id);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeoutCts.CancelAfter(_forwardTimeout);
            var priorAborted = context.RequestAborted;
            context.RequestAborted = timeoutCts.Token;

            ForwarderError error;
            try
            {
                error = await _forwarder.SendAsync(
                    context,
                    modelConfig.Url,
                    _httpClient,
                    ForwarderRequestConfig.Empty,
                    transformer).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested &&
                                                       !priorAborted.IsCancellationRequested)
            {
                _circuitBreakers.RecordFailure(modelConfig.Id);
                if (!context.Response.HasStarted)
                {
                    await context.WriteGatewayErrorAsync(
                        _errors.Write(
                            GatewayErrorCode.UpstreamError,
                            message: "Request timed out while forwarding to backend."),
                        CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }
            finally
            {
                context.RequestAborted = priorAborted;
            }

            if (error == ForwarderError.None)
            {
                _circuitBreakers.RecordSuccess(modelConfig.Id);
                RecordRecentRequest(context, modelConfig.Id, started, requestInfo.Stream, success: true);
                return;
            }

            _circuitBreakers.RecordFailure(modelConfig.Id);
            if (!context.Response.HasStarted)
            {
                _logger.LogWarning("Forwarder error {Error} for model {ModelId}", error, modelConfig.Id);
                await context.WriteGatewayErrorAsync(
                    _errors.Write(GatewayErrorCode.UpstreamError),
                    context.RequestAborted).ConfigureAwait(false);
            }

            RecordRecentRequest(context, modelConfig.Id, started, requestInfo.Stream, success: false);
            }
            finally
            {
                if (streamSlotAcquired)
                {
                    _rateLimitStore.ReleaseStreamSlot(ratePartitionKey);
                }
            }
        }
    }

    private void RecordRecentRequest(
        HttpContext context,
        string modelId,
        DateTimeOffset started,
        bool isStreaming,
        bool success)
    {
        var requestId = context.Items.TryGetValue(RequestIdKeys.HttpContextItemKey, out var rid)
            ? rid?.ToString() ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");

        string? tenantId = null;
        if (context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var tenantValue) &&
            tenantValue is TenantContext tenant)
        {
            tenantId = tenant.TenantId;
        }

        var durationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
        var statusCode = context.Response.HasStarted ? context.Response.StatusCode : (success ? 200 : 502);

        _recentRequestStore.Record(new RecentRequestEntry
        {
            RequestId = requestId,
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? string.Empty,
            ModelId = modelId,
            TenantId = tenantId,
            StatusCode = statusCode,
            DurationMs = durationMs,
            IsStreaming = isStreaming,
            TimestampUtc = DateTimeOffset.UtcNow,
        });
    }

    private static string ResolveRateLimitPartitionKey(HttpContext context) =>
        context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var value) &&
        value is TenantContext tenant &&
        !string.IsNullOrWhiteSpace(tenant.TenantId)
            ? tenant.TenantId
            : "anonymous";
}
