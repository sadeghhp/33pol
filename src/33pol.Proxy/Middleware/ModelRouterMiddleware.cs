using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Forwarding;
using Pol33.Core.Errors;
using Pol33.Core.Models;
using Pol33.Core.Identity;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
using System.Security.Claims;
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
    private readonly IUsageRecorder _usageRecorder;
    private readonly IGatewayMetricsCollector _metricsCollector;
    private readonly ModelCircuitBreakerRegistry _circuitBreakers;
    private readonly BulkheadRegistry _bulkhead;
    private readonly IRateLimitPolicyResolver _rateLimitPolicyResolver;
    private readonly IDistributedRateLimitStore _rateLimitStore;
    private readonly IInferenceHttpForwarder _forwarder;
    private readonly InferenceForwardTimeouts _forwardTimeouts;
    private readonly IUpstreamBearerTokenResolver _upstreamBearerTokenResolver;
    private readonly IBudgetEnforcementService _budgetEnforcement;
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
        IUsageRecorder usageRecorder,
        IGatewayMetricsCollector metricsCollector,
        ModelCircuitBreakerRegistry circuitBreakers,
        BulkheadRegistry bulkhead,
        IRateLimitPolicyResolver rateLimitPolicyResolver,
        IDistributedRateLimitStore rateLimitStore,
        IInferenceHttpForwarder forwarder,
        IOptions<GatewayOptions> options,
        IUpstreamBearerTokenResolver upstreamBearerTokenResolver,
        IBudgetEnforcementService budgetEnforcement,
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
        _usageRecorder = usageRecorder;
        _metricsCollector = metricsCollector;
        _circuitBreakers = circuitBreakers;
        _bulkhead = bulkhead;
        _rateLimitPolicyResolver = rateLimitPolicyResolver;
        _rateLimitStore = rateLimitStore;
        _forwarder = forwarder;
        _forwardTimeouts = InferenceForwardTimeouts.FromResilience(options.Value.Resilience);
        _upstreamBearerTokenResolver = upstreamBearerTokenResolver;
        _budgetEnforcement = budgetEnforcement;
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

        // PublicModelDetectionMiddleware has already parsed this body. Reusing its result is what
        // keeps the most expensive step on the inference path to one pass; the fallback covers
        // pipelines (and tests) where that middleware did not run.
        InferenceRequestInfo requestInfo;
        if (InferenceRequestParseCache.TryGet(context, out var cachedInfo))
        {
            if (cachedInfo is null)
            {
                await context.WriteGatewayErrorAsync(
                    _errors.Write(GatewayErrorCode.InvalidJson),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            requestInfo = cachedInfo.Value;
        }
        else
        {
            try
            {
                context.Request.Body.Position = 0;
                requestInfo = await InferenceRequestParser.ParseAsync(context.Request.Body, context.RequestAborted)
                    .ConfigureAwait(false);
                InferenceRequestParseCache.SetParsed(context, requestInfo);
            }
            catch (JsonException)
            {
                await context.WriteGatewayErrorAsync(
                    _errors.Write(GatewayErrorCode.InvalidJson),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }
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
            _metricsCollector.RecordModelResolve("not_found");
            await context.WriteGatewayErrorAsync(
                _errors.Write(
                    GatewayErrorCode.ModelNotFound,
                    message: $"Model '{requestInfo.Model}' not found."),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        _metricsCollector.RecordModelResolve(
            string.Equals(requestInfo.Model, modelConfig.Id, StringComparison.OrdinalIgnoreCase)
                ? "resolved"
                : "alias");
        _metricsCollector.RecordInferenceRouted(modelConfig.Id, ClassifyRoute(context.Request.Path), requestInfo.Stream);

        if (_authState.IsAuthenticationRequired && !modelConfig.AllowsPublicGatewayAccess())
        {
            if (context.User.Identity?.IsAuthenticated != true ||
                !Guid.TryParse(context.User.FindFirstValue(GatewayAuthClaims.TenantId), out var tenantId) ||
                !Guid.TryParse(context.User.FindFirstValue(GatewayAuthClaims.ApiKeyId), out var apiKeyId))
            {
                await context.WriteGatewayErrorAsync(
                    _errors.Write(GatewayErrorCode.InvalidApiKey),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var modelGrants = scope.ServiceProvider.GetRequiredService<IModelGrantService>();
            if (!await modelGrants.IsModelAllowedAsync(tenantId, apiKeyId, modelConfig.Id, context.RequestAborted)
                    .ConfigureAwait(false))
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
            await RejectAtAdmissionAsync(
                context,
                modelConfig.Id,
                requestInfo.Stream,
                _errors.Write(
                    GatewayErrorCode.BackendUnhealthy,
                    message: $"Backend for model '{modelConfig.Id}' is unhealthy."),
                outcome: "backend_unhealthy").ConfigureAwait(false);
            return;
        }

        if (!_circuitBreakers.TryEnter(modelConfig.Id))
        {
            _logger.LogWarning(
                "Rejected request for model {ModelId}: circuit breaker is {CircuitState}",
                modelConfig.Id,
                ModelCircuitBreakerRegistry.ToStateLabel(_circuitBreakers.GetBreaker(modelConfig.Id).State));
            await RejectAtAdmissionAsync(
                context,
                modelConfig.Id,
                requestInfo.Stream,
                _errors.Write(GatewayErrorCode.CircuitOpen),
                outcome: "circuit_open").ConfigureAwait(false);
            return;
        }

        // Admission consumed the breaker's half-open probe permit. The lease guarantees it is released
        // on every exit path — including thrown exceptions — so the breaker cannot wedge in HalfOpen.
        using var circuitLease = new CircuitBreakerProbeLease(_circuitBreakers, modelConfig.Id);

        var bulkheadLease = await _bulkhead.TryAcquireAsync(modelConfig.Id, context.RequestAborted).ConfigureAwait(false);
        if (bulkheadLease is null)
        {
            // Gateway-side saturation, not backend ill health: abandon the probe rather than
            // recording a failure that would trip the breaker on a healthy backend.
            //
            // Reported as a concurrency limit (429 + Retry-After), not as UpstreamError. A 502
            // backend_error told clients the model itself had failed, so OpenAI-compatible routers
            // marked it down and failed over — when the correct signal is simply "retry shortly".
            await RejectAtAdmissionAsync(
                context,
                modelConfig.Id,
                requestInfo.Stream,
                _errors.Write(
                    GatewayErrorCode.ConcurrencyLimitExceeded,
                    message: "Too many concurrent requests for this model. Retry shortly."),
                outcome: "bulkhead_full",
                retryAfterSeconds: 1).ConfigureAwait(false);
            return;
        }

        using (bulkheadLease)
        {
            var ratePartitionKey = RateLimitPartition.Resolve(context);
            var planSlug = context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var rateTenantItem) &&
                           rateTenantItem is TenantContext rateTenantContext
                ? rateTenantContext.PlanSlug
                : null;
            var ratePolicy = _rateLimitPolicyResolver.Resolve(planSlug, ratePartitionKey);

            // The stream-concurrency cap is part of rate limiting, so the same master switch governs it;
            // otherwise "rate limiting off" would still throttle streaming requests.
            var rateLimitingEnabled = _rateLimitPolicyResolver.IsEnabled();

            var streamSlotAcquired = false;
            if (requestInfo.Stream && rateLimitingEnabled)
            {
                var streamAcquire = _rateLimitStore.TryAcquireStreamSlot(ratePartitionKey, ratePolicy);
                if (!streamAcquire.IsAcquired)
                {
                    // Client-tier concurrency limit, not a backend signal. It is governed by the
                    // rate-limit master switch, so it is counted as a rate-limit rejection too —
                    // otherwise the console's "Rate-limited" stat silently omitted stream caps.
                    _metricsCollector.RecordRateLimitRejection("stream_concurrency");
                    await RejectAtAdmissionAsync(
                        context,
                        modelConfig.Id,
                        requestInfo.Stream,
                        _errors.Write(GatewayErrorCode.ConcurrencyLimitExceeded),
                        outcome: "stream_concurrency",
                        retryAfterSeconds: streamAcquire.RetryAfterSeconds).ConfigureAwait(false);
                    return;
                }

                streamSlotAcquired = true;
            }

            try
            {
                context.Request.Body.Position = 0;

            var started = DateTimeOffset.UtcNow;
            context.Items[InferenceForwardingContextKeys.StartedUtc] = started;
            context.Items[InferenceForwardingContextKeys.ModelId] = modelConfig.Id;
            using var inferenceScope = _requestTracker.BeginInferenceRequest(modelConfig.Id, requestInfo.Stream);

            var requestId = ResolveRequestId(context);

            // Published before the forward, not after it: this is what puts a running inference on
            // the dashboard while it runs instead of only once it has already finished.
            using var inFlightLease = PublishInFlight(context, requestId, modelConfig.Id, started, requestInfo.Stream);

            TenantContext? usageTenant = null;
            if (context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var usageTenantValue) &&
                usageTenantValue is TenantContext tenantContextForUsage)
            {
                usageTenant = tenantContextForUsage;
            }

            // Length of the body actually forwarded. Used only to approximate prompt tokens when a
            // stream ends before its authoritative usage frame — the upstream read and charged for
            // the whole prompt regardless of how the response ended.
            var requestBodyBytes = context.Request.Body.CanSeek ? context.Request.Body.Length : 0L;

            // The same partition key the quota middleware checked this request under. Committing the
            // usage anywhere else means the next check never sees it.
            var usageCapture = new InferenceUsageCapture(
                _usageRecorder,
                _metricsCollector,
                modelConfig.Id,
                requestId,
                started,
                usageTenant,
                requestBodyBytes,
                quotaPartition: ratePartitionKey);

            var upstreamBearerToken = _upstreamBearerTokenResolver.ResolveBearerToken(modelConfig.UpstreamAuth);
            if (modelConfig.UpstreamAuth is not null && string.IsNullOrWhiteSpace(upstreamBearerToken))
            {
                // Permanent gateway misconfiguration. Recording it as a backend failure would trip the
                // breaker on every request and mask the real cause behind an opaque circuit_open error.
                _logger.LogError(
                    "Upstream auth token not configured for model {ModelId}; check UpstreamAuth configuration",
                    modelConfig.Id);
                inferenceScope.SetOutcome(false, "upstream_error");
                _metricsCollector.RecordForwardAttempt(modelConfig.Id, "upstream_error");
                await context.WriteGatewayErrorAsync(
                    _errors.Write(
                        GatewayErrorCode.UpstreamError,
                        message: "Upstream auth token not configured for this model."),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            // Reserve the estimated max cost against hard-stop budgets before forwarding, so concurrent
            // requests (whose actual cost is unknown until the response returns) cannot collectively
            // overshoot a hard cap.
            // The prompt side is reserved too: for long-context traffic the input is the dominant
            // cost, and pricing only max_tokens let concurrent large-prompt requests overshoot a hard
            // cap by orders of magnitude while each reserved a few thousand output tokens.
            var budgetReservation = await _budgetEnforcement.TryReserveAsync(
                usageTenant?.TenantId,
                requestId,
                modelConfig.Id,
                requestInfo.MaxTokens,
                requestBodyBytes,
                context.RequestAborted).ConfigureAwait(false);
            if (!budgetReservation.IsAllowed)
            {
                inferenceScope.SetOutcome(false, "budget_exceeded");
                _metricsCollector.RecordForwardAttempt(modelConfig.Id, "budget_exceeded");
                await context.WriteGatewayErrorAsync(
                    _errors.Write(GatewayErrorCode.QuotaExceeded),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            // Only a successful forward produces a usage event, and only that event's persistence
            // settles the reservation. Every other exit — including a thrown exception — must hand
            // the headroom back here; relying on the TTL meant a failed request held budget for the
            // full TTL, and any request outliving the TTL had its reservation swept while still
            // in flight, which is exactly the overshoot the ledger exists to prevent.
            var settledByUsage = false;
            try
            {
            var transformer = new StreamingHttpTransformer(
                requestInfo.Stream,
                requestInfo.Model,
                modelConfig.Id,
                usageCapture,
                stripClientAuthHeaders: true,
                upstreamBearerToken: upstreamBearerToken,
                // Where the client's model value sits in the body. Lets an alias be swapped for the
                // canonical id by splicing bytes rather than rebuilding the document.
                modelValueRange: requestInfo.ModelValueRange);

            // The forwarder owns both deadlines (header timeout vs response-idle timeout) and reports
            // which one fired through its return value, so RequestAborted is left as the client's
            // own token. Overwriting it with a total-duration deadline is what used to truncate
            // healthy long streams and attribute the truncation to the backend.
            //
            // The header deadline is widened in proportion to the prompt actually being forwarded: a
            // fixed one made a large-context request time out purely because the backend was still
            // reading its prompt, and the breaker then counted that against a backend that was
            // working correctly.
            var error = await _forwarder.SendAsync(
                context,
                modelConfig.Url,
                upstreamBearerToken,
                transformer,
                requestInfo.Stream,
                _forwardTimeouts.ForRequestBody(requestBodyBytes),
                context.RequestAborted).ConfigureAwait(false);

            if (error == ForwarderError.None)
            {
                // Usage capture runs during response disposal, inside SendAsync, so by now we know
                // whether an event was actually enqueued. If it was not (unparseable or absent usage)
                // nothing downstream will settle the reservation, so the finally must release it.
                settledByUsage = usageCapture.HasEnqueuedUsage;

                // "Forwarded" is not "succeeded". The forwarder reports None for any proxied status
                // code, and a backend that degrades into fast 5xx responses is exactly as unhealthy
                // as one that times out — counting those as breaker successes closed a half-open
                // breaker on the first 500 and kept it closed forever, while metrics and the recent
                // requests feed reported the failure as success.
                if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
                {
                    circuitLease.RecordFailure();
                    inferenceScope.SetOutcome(false, "upstream_5xx");
                    _metricsCollector.RecordForwardAttempt(modelConfig.Id, "upstream_5xx");
                    RecordRecentRequest(context, modelConfig.Id, started, requestInfo.Stream, success: false);
                    return;
                }

                // A proxied 4xx splits the two questions the outcome used to answer at once. The
                // backend answered, so it stays healthy as far as the breaker is concerned — but the
                // client got an error, so it counts as one on the dashboard. Conflating them meant a
                // model rejecting every call with 400 reported "0 errors, 0.00% error rate" beside a
                // feed full of red rows.
                var clientError = context.Response.StatusCode >= StatusCodes.Status400BadRequest;
                circuitLease.RecordSuccess();
                if (clientError)
                {
                    inferenceScope.SetOutcome(false, "upstream_4xx");
                    _metricsCollector.RecordForwardAttempt(modelConfig.Id, "upstream_4xx");
                }
                else
                {
                    inferenceScope.SetOutcome(true);
                    _metricsCollector.RecordForwardAttempt(modelConfig.Id, "success");
                }

                RecordRecentRequest(context, modelConfig.Id, started, requestInfo.Stream, success: !clientError);
                return;
            }

            if (error == ForwarderError.RequestTimedOut)
            {
                // Headers never arrived: the backend failed to respond at all, a real health signal.
                circuitLease.RecordFailure();
                inferenceScope.SetOutcome(false, "upstream_timeout");
                _metricsCollector.RecordForwardAttempt(modelConfig.Id, "upstream_timeout");
                if (!context.Response.HasStarted)
                {
                    await context.WriteGatewayErrorAsync(
                        _errors.Write(
                            GatewayErrorCode.UpstreamError,
                            message: "Request timed out while forwarding to backend."),
                        CancellationToken.None).ConfigureAwait(false);
                }

                RecordRecentRequest(context, modelConfig.Id, started, requestInfo.Stream, success: false);
                return;
            }

            if (error == ForwarderError.ResponseBodyDestination)
            {
                // The upstream answered and then stalled while sending the body. The backend proved
                // it was reachable and producing, so this is not evidence of ill health — abandon the
                // probe (via lease dispose) rather than counting a failure.
                _logger.LogWarning(
                    "Response body for model {ModelId} stalled past the idle timeout after {ElapsedMs}ms",
                    modelConfig.Id,
                    (DateTimeOffset.UtcNow - started).TotalMilliseconds);
                inferenceScope.SetOutcome(false, "stream_idle_timeout");
                _metricsCollector.RecordForwardAttempt(modelConfig.Id, "stream_idle_timeout");

                // A streaming response has already flushed its first bytes, but a non-streaming one
                // can stall before a single byte is written. Without this the client received the
                // upstream's status code and an empty body rather than an error it can act on.
                if (!context.Response.HasStarted)
                {
                    await context.WriteGatewayErrorAsync(
                        _errors.Write(
                            GatewayErrorCode.UpstreamError,
                            message: "Backend stopped sending the response body."),
                        CancellationToken.None).ConfigureAwait(false);
                }

                RecordRecentRequest(context, modelConfig.Id, started, requestInfo.Stream, success: false);
                return;
            }

            if (error == ForwarderError.RequestCanceled)
            {
                // The client hung up. This is not evidence the backend is unhealthy, and counting it
                // would let a burst of disconnects trip the breaker on a perfectly good backend.
                // Leaving the lease unrecorded abandons the probe on dispose.
                inferenceScope.SetOutcome(false, "client_canceled");
                _metricsCollector.RecordForwardAttempt(modelConfig.Id, "client_canceled");
                RecordRecentRequest(context, modelConfig.Id, started, requestInfo.Stream, success: false);
                return;
            }

            circuitLease.RecordFailure();
            inferenceScope.SetOutcome(false, "upstream_error");
            _metricsCollector.RecordForwardAttempt(modelConfig.Id, "upstream_error");
            if (!context.Response.HasStarted)
            {
                _logger.LogWarning("Forwarder error {Error} for model {ModelId}", error, modelConfig.Id);
                await context.WriteGatewayErrorAsync(
                    _errors.Write(GatewayErrorCode.UpstreamError),
                    CancellationToken.None).ConfigureAwait(false);
            }

            RecordRecentRequest(context, modelConfig.Id, started, requestInfo.Stream, success: false);
            }
            finally
            {
                if (!settledByUsage)
                {
                    // Idempotent: releasing a request id the ledger no longer holds is a no-op, so
                    // this is safe even if usage settlement won the race.
                    _budgetEnforcement.ReleaseReservation(requestId);
                }
            }
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

    /// <summary>
    /// Answers a request the gateway refused before forwarding, and records it everywhere the
    /// console reads from: the request and error totals, the per-model error breakdown, and the
    /// live feed.
    /// </summary>
    /// <remarks>
    /// These four paths (unhealthy backend, open circuit, full bulkhead, exhausted stream slot)
    /// previously reported only to Prometheus, so a saturated gateway rejecting every call still
    /// rendered as "0 errors, 0.00% error rate" on the dashboard. The recording happens after the
    /// response is written so the feed row carries the real status code and error header.
    /// </remarks>
    private async Task RejectAtAdmissionAsync(
        HttpContext context,
        string modelId,
        bool isStreaming,
        WrittenErrorResponse error,
        string outcome,
        int? retryAfterSeconds = null)
    {
        var started = DateTimeOffset.UtcNow;
        _metricsCollector.RecordForwardAttempt(modelId, outcome);

        await context.WriteGatewayErrorAsync(error, context.RequestAborted, retryAfterSeconds)
            .ConfigureAwait(false);

        _requestTracker.RecordRejectedRequest(modelId, outcome);
        RecordRecentRequest(context, modelId, started, isStreaming, success: false);
    }

    /// <summary>
    /// Publishes the request to the live feed the moment forwarding begins, and retires the entry on
    /// every exit path. Until the upstream answers the row carries status 0 and a duration that
    /// grows with each dashboard poll.
    /// </summary>
    private IDisposable PublishInFlight(
        HttpContext context,
        string requestId,
        string modelId,
        DateTimeOffset started,
        bool isStreaming)
    {
        _recentRequestStore.BeginInFlight(new RecentRequestEntry
        {
            RequestId = requestId,
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? string.Empty,
            ModelId = modelId,
            TenantId = ResolveTenantId(context),
            StatusCode = 0,
            DurationMs = 0,
            IsStreaming = isStreaming,
            ErrorCode = null,
            TimestampUtc = started,
            IsInFlight = true,
        });

        return new InFlightRequestLease(_recentRequestStore, requestId);
    }

    /// <remarks>
    /// A completed entry for the same request id supersedes the in-flight one inside the store, so
    /// this lease is the guarantee for the paths that never record a completion — a thrown exception
    /// above all. Releasing an id the store no longer holds is a no-op.
    /// </remarks>
    private sealed class InFlightRequestLease(IRecentRequestStore store, string requestId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                store.CompleteInFlight(requestId);
            }
        }
    }

    private static string? ResolveTenantId(HttpContext context) =>
        context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var tenantValue) &&
        tenantValue is TenantContext tenant
            ? tenant.TenantId
            : null;

    private void RecordRecentRequest(
        HttpContext context,
        string modelId,
        DateTimeOffset started,
        bool isStreaming,
        bool success)
    {
        var requestId = ResolveRequestId(context);
        var tenantId = ResolveTenantId(context);

        var durationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds;

        // Prefer whatever status the gateway actually set — an admission rejection has already
        // written its 429 or 503 even though the body may not have flushed yet, and keying off
        // HasStarted alone reported every one of them to the console as a generic 502.
        var statusCode = context.Response.StatusCode;
        if (!success && !context.Response.HasStarted && statusCode < StatusCodes.Status400BadRequest)
        {
            statusCode = StatusCodes.Status502BadGateway;
        }

        var errorCode = ResolveErrorCode(context);

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
            ErrorCode = errorCode,
            TimestampUtc = DateTimeOffset.UtcNow,
        });
    }

    private static string? ResolveErrorCode(HttpContext context)
    {
        if (!context.Response.Headers.TryGetValue(GatewayHeaders.ErrorCode, out var values))
        {
            return null;
        }

        var code = values.ToString();
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    /// <summary>
    /// Resolves the request id once and writes any generated value back into
    /// <see cref="HttpContext.Items"/>, so the usage event, the budget reservation and the
    /// recent-request entry all carry the same id.
    /// </summary>
    /// <remarks>
    /// Minting a fresh GUID per call (the previous behaviour) meant that when RequestIdMiddleware had
    /// not run, the reservation was keyed to an id no release would ever match — it leaked until the
    /// TTL swept it — and the dashboard entry could not be correlated with its billing event.
    /// </remarks>
    private static string ResolveRequestId(HttpContext context)
    {
        if (context.Items.TryGetValue(RequestIdKeys.HttpContextItemKey, out var existing) &&
            existing?.ToString() is { Length: > 0 } requestId)
        {
            return requestId;
        }

        var generated = $"req_{Guid.NewGuid():N}";
        context.Items[RequestIdKeys.HttpContextItemKey] = generated;
        return generated;
    }

    private static string ClassifyRoute(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return "chat";
        }

        if (value.EndsWith("/v1/completions", StringComparison.OrdinalIgnoreCase))
        {
            return "completions";
        }

        if (value.EndsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            return "embeddings";
        }

        if (value.EndsWith("/v1/rerank", StringComparison.OrdinalIgnoreCase))
        {
            return "rerank";
        }

        return "unknown";
    }
}
