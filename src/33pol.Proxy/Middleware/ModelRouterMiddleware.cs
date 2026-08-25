using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Diagnostics;
using Pol33.Core.Forwarding;
using Pol33.Core.Errors;
using Pol33.Core.Models;
using Pol33.Core.Identity;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
using Pol33.Core.Usage;
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
    private readonly IGatewayErrorRecorder _errorRecorder;
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
        IGatewayErrorRecorder errorRecorder,
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
        _errorRecorder = errorRecorder;
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
            _metricsCollector.RecordModelResolve("not_found", requestInfo.Model);
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
                // Counted like the other admission refusals so a key hammering a model it was never
                // granted shows up on the Overview instead of vanishing between the counters.
                _metricsCollector.RecordGrantDenial(tenantId.ToString(), modelConfig.Id);
                await RejectAtAdmissionAsync(
                    context,
                    modelConfig.Id,
                    requestInfo.Stream,
                    _errors.Write(
                        GatewayErrorCode.InsufficientScope,
                        message: "The API key is not granted access to this model."),
                    outcome: "insufficient_scope").ConfigureAwait(false);
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
                    _metricsCollector.RecordRateLimitRejection("stream_concurrency", ratePartitionKey, modelConfig.Id);
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

            // Declared outside the try so the catch below can mark it failed. Every exception that
            // escapes the forward would otherwise dispose a scope with no outcome, which the tracker
            // reads as success — a 502 to the client that never reached the Overview error count.
            IInferenceRequestScope? inferenceScope = null;
            try
            {
                context.Request.Body.Position = 0;

            var started = DateTimeOffset.UtcNow;
            context.Items[InferenceForwardingContextKeys.StartedUtc] = started;
            context.Items[InferenceForwardingContextKeys.ModelId] = modelConfig.Id;
            var scopeTenantId = context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var scopeTenantValue) &&
                                scopeTenantValue is TenantContext scopeTenant
                ? scopeTenant.TenantId
                : null;
            inferenceScope = _requestTracker.BeginInferenceRequest(modelConfig.Id, requestInfo.Stream, scopeTenantId);

            var requestId = ResolveRequestId(context);

            // Published before the forward, not after it: this is what puts a running inference on
            // the dashboard while it runs instead of only once it has already finished.
            TenantContext? usageTenant = null;
            if (context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var usageTenantValue) &&
                usageTenantValue is TenantContext tenantContextForUsage)
            {
                usageTenant = tenantContextForUsage;
            }

            using var inFlightLease = PublishInFlight(
                context, requestId, modelConfig.Id, started, requestInfo.Stream, usageTenant?.CostCenter);

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
                RecordRecentRequest(
                    context,
                    modelConfig.Id,
                    started,
                    requestInfo.Stream,
                    success: false,
                    outcome: "upstream_auth_missing",
                    upstreamUrl: modelConfig.Url,
                    usage: usageCapture);
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
                _metricsCollector.RecordBudgetRejection(usageTenant?.TenantId, budgetReservation.BudgetName, modelConfig.Id);
                await context.WriteGatewayErrorAsync(
                    _errors.Write(GatewayErrorCode.QuotaExceeded),
                    context.RequestAborted).ConfigureAwait(false);

                // SetOutcome above already counted this as an error. Without recording it here too,
                // the dashboard counter climbs while the feed and the Errors tab show nothing —
                // the operator sees a number with no way to find out what it refers to.
                RecordRecentRequest(
                    context,
                    modelConfig.Id,
                    started,
                    requestInfo.Stream,
                    success: false,
                    outcome: "budget_exceeded",
                    upstreamUrl: modelConfig.Url,
                    usage: usageCapture);
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
                    RecordRecentRequest(
                        context,
                        modelConfig.Id,
                        started,
                        requestInfo.Stream,
                        success: false,
                        outcome: "upstream_5xx",
                        upstreamUrl: modelConfig.Url,
                    usage: usageCapture);
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

                RecordRecentRequest(
                    context,
                    modelConfig.Id,
                    started,
                    requestInfo.Stream,
                    success: !clientError,
                    outcome: clientError ? "upstream_4xx" : "success",
                    upstreamUrl: modelConfig.Url,
                    usage: usageCapture);
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

                RecordRecentRequest(
                    context,
                    modelConfig.Id,
                    started,
                    requestInfo.Stream,
                    success: false,
                    outcome: "upstream_timeout",
                    upstreamUrl: modelConfig.Url,
                    usage: usageCapture);
                return;
            }

            if (error == ForwarderError.ResponseBodyCanceled)
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

                RecordRecentRequest(
                    context,
                    modelConfig.Id,
                    started,
                    requestInfo.Stream,
                    success: false,
                    outcome: "stream_idle_timeout",
                    upstreamUrl: modelConfig.Url,
                    usage: usageCapture);
                return;
            }

            if (error == ForwarderError.ResponseBodyDestination)
            {
                // The upstream answered and then broke the body off — connection reset, premature
                // EOF, bad framing. That is the backend's failure, not the client's: it counts against
                // the breaker and shows up in the error store like any other upstream error. Before
                // this was distinguished it was reported as client_canceled, which hid a flapping
                // backend from the breaker and the operator, and a non-streaming client got the
                // upstream's 200 with a truncated body instead of an error it could act on.
                circuitLease.RecordFailure();
                inferenceScope.SetOutcome(false, "upstream_body_error");
                _metricsCollector.RecordForwardAttempt(modelConfig.Id, "upstream_body_error");
                if (!context.Response.HasStarted)
                {
                    await context.WriteGatewayErrorAsync(
                        _errors.Write(
                            GatewayErrorCode.UpstreamError,
                            message: "Backend connection failed while sending the response body."),
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning(
                        "Upstream response body for model {ModelId} failed after {ElapsedMs}ms with the response already started; the client received a truncated body",
                        modelConfig.Id,
                        (DateTimeOffset.UtcNow - started).TotalMilliseconds);
                }

                RecordRecentRequest(
                    context,
                    modelConfig.Id,
                    started,
                    requestInfo.Stream,
                    success: false,
                    outcome: "upstream_body_error",
                    upstreamUrl: modelConfig.Url,
                    usage: usageCapture);
                return;
            }

            if (error == ForwarderError.RequestCanceled)
            {
                // The client hung up. This is not evidence the backend is unhealthy, and counting it
                // would let a burst of disconnects trip the breaker on a perfectly good backend.
                // Leaving the lease unrecorded abandons the probe on dispose.
                inferenceScope.SetClientCanceled();
                _metricsCollector.RecordForwardAttempt(modelConfig.Id, "client_canceled");
                RecordRecentRequest(
                    context,
                    modelConfig.Id,
                    started,
                    requestInfo.Stream,
                    success: false,
                    outcome: "client_canceled",
                    upstreamUrl: modelConfig.Url,
                    usage: usageCapture);
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

            RecordRecentRequest(
                context,
                modelConfig.Id,
                started,
                requestInfo.Stream,
                success: false,
                outcome: "upstream_error",
                upstreamUrl: modelConfig.Url,
                    usage: usageCapture);
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
            catch (Exception) when (inferenceScope is not null)
            {
                if (context.RequestAborted.IsCancellationRequested)
                {
                    inferenceScope.SetClientCanceled();
                }
                else
                {
                    inferenceScope.SetOutcome(false, "unhandled");
                }

                throw;
            }
            finally
            {
                inferenceScope?.Dispose();
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
        RecordRecentRequest(context, modelId, started, isStreaming, success: false, outcome: outcome);
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
        bool isStreaming,
        string? costCenter)
    {
        _recentRequestStore.BeginInFlight(new RecentRequestEntry
        {
            RequestId = requestId,
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? string.Empty,
            ModelId = modelId,
            TenantId = ResolveTenantId(context),
            CostCenter = costCenter,
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

    private static string? ResolveTenantId(HttpContext context) => ResolveTenant(context)?.TenantId;

    private static TenantContext? ResolveTenant(HttpContext context) =>
        context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var tenantValue) &&
        tenantValue is TenantContext tenant
            ? tenant
            : null;

    /// <param name="outcome">
    /// How the request ended, e.g. <c>upstream_5xx</c>. Already computed at every call site for the
    /// metrics counter; passing it here is what lets the Errors tab group by failure shape rather
    /// than by status code alone.
    /// </param>
    /// <param name="upstreamUrl">The model's configured base URL, sanitized before it is stored.</param>
    /// <param name="usage">
    /// The response's usage capture, when the request got far enough to have one. Its token counts
    /// go on the feed row immediately; the costs follow from the billing writer via
    /// <see cref="IRecentRequestStore.AttachUsage"/>, so the row is marked <c>pending</c> only when
    /// an event was actually accepted for pricing.
    /// </param>
    private void RecordRecentRequest(
        HttpContext context,
        string modelId,
        DateTimeOffset started,
        bool isStreaming,
        bool success,
        string? outcome = null,
        string? upstreamUrl = null,
        InferenceUsageCapture? usage = null)
    {
        var requestId = ResolveRequestId(context);
        var tenant = ResolveTenant(context);
        var tenantId = tenant?.TenantId;

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

        var entry = new RecentRequestEntry
        {
            RequestId = requestId,
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? string.Empty,
            ModelId = modelId,
            TenantId = tenantId,
            CostCenter = tenant?.CostCenter,
            StatusCode = statusCode,
            DurationMs = durationMs,
            IsStreaming = isStreaming,
            TimeToFirstTokenMs = context.Items.TryGetValue(InferenceForwardingContextKeys.TimeToFirstTokenMs, out var ttft) && ttft is double ttftMs
                ? ttftMs
                : null,
            ErrorCode = errorCode,
            TimestampUtc = DateTimeOffset.UtcNow,
        };

        if (usage?.CapturedUsage is UsageEvent captured)
        {
            entry = entry.WithUsage(RecentRequestUsageMapper.FromUsageEvent(
                captured,
                pricingStatus: usage.HasEnqueuedUsage
                    ? RecentRequestUsage.StatusPending
                    : RecentRequestUsage.StatusUnpriced));
        }

        _recentRequestStore.Record(entry);

        if (!success)
        {
            RecordInferenceError(
                context,
                modelId,
                requestId,
                tenantId,
                statusCode,
                durationMs,
                errorCode,
                outcome,
                upstreamUrl);
        }
    }

    /// <summary>
    /// Publishes a failed inference to the durable error store, with the model, upstream, outcome
    /// and remediation hint the Errors tab needs to make it actionable.
    /// </summary>
    /// <remarks>
    /// A client hang-up is skipped: the caller walked away, the gateway and the backend both did
    /// their jobs, and filling the error store with disconnects buries the faults an operator is
    /// looking for. The aggregate error counter still counts them, so the Errors tab can legitimately
    /// show fewer errors than the Overview tile.
    /// </remarks>
    private void RecordInferenceError(
        HttpContext context,
        string modelId,
        string requestId,
        string? tenantId,
        int statusCode,
        double durationMs,
        string? errorCode,
        string? outcome,
        string? upstreamUrl)
    {
        if (string.Equals(outcome, "client_canceled", StringComparison.Ordinal))
        {
            return;
        }

        var path = context.Request.Path;
        var upstreamException = context.Items.TryGetValue(GatewayErrorContextKeys.UpstreamException, out var stashed)
            ? stashed as Exception
            : null;

        _errorRecorder.Record(new GatewayErrorRecord
        {
            Id = $"err_{Guid.NewGuid():N}",
            Fingerprint = string.Empty,
            OccurredAt = DateTimeOffset.UtcNow,
            Level = ClassifyErrorLevel(statusCode, outcome).ToString(),
            Source = GatewayErrorSourceNames.Proxy,
            Category = nameof(ModelRouterMiddleware),
            EventCode = errorCode ?? outcome,
            Message = BuildErrorMessage(modelId, statusCode, outcome),
            ExceptionType = upstreamException?.GetType().FullName,
            StackTrace = upstreamException?.ToString(),
            Method = context.Request.Method,
            Path = path.Value,
            RouteKind = ClassifyRoute(path),
            StatusCode = statusCode,
            ModelId = modelId,
            UpstreamTarget = upstreamUrl,
            Outcome = outcome,
            TenantId = tenantId,
            ApiKeyId = ResolveApiKeyId(context),
            RequestId = requestId,
            DurationMs = durationMs,
            UpstreamBodySnippet = context.Items.TryGetValue(
                GatewayErrorContextKeys.UpstreamBodySnippet,
                out var snippet)
                ? snippet as string
                : null,
            // The transport hint ("nothing is listening on the model's URL") beats the status hint
            // whenever there is an exception to read it from.
            Hint = GatewayLogHints.ForException(upstreamException)
                ?? GatewayLogHints.ForUpstreamStatus(statusCode, upstreamUrl, path.Value, modelId),
        });

        // Tells the terminal exception handler this failure is already accounted for.
        context.Items[GatewayErrorContextKeys.ErrorCaptured] = true;
    }

    private static GatewayLogLevel ClassifyErrorLevel(int statusCode, string? outcome) => outcome switch
    {
        // A permanently misconfigured upstream will never recover on its own, unlike a backend
        // having a bad minute — it needs an operator, so it is ranked above an ordinary 5xx.
        "upstream_auth_missing" => GatewayLogLevel.Critical,
        "upstream_4xx" or "budget_exceeded" or "backend_unhealthy" or "circuit_open"
            or "bulkhead_full" or "stream_concurrency" => GatewayLogLevel.Warning,
        _ => statusCode >= StatusCodes.Status500InternalServerError
            ? GatewayLogLevel.Error
            : GatewayLogLevel.Warning,
    };

    private static string BuildErrorMessage(string modelId, int statusCode, string? outcome) => outcome switch
    {
        "upstream_timeout" => $"Upstream timed out for model '{modelId}'.",
        "stream_idle_timeout" => $"Upstream stopped sending the response body for model '{modelId}'.",
        "upstream_body_error" => $"Upstream connection failed while sending the response body for model '{modelId}'.",
        "backend_unhealthy" => $"Rejected: no healthy backend for model '{modelId}'.",
        "circuit_open" => $"Rejected: circuit breaker open for model '{modelId}'.",
        "bulkhead_full" => $"Rejected: concurrency limit reached for model '{modelId}'.",
        "stream_concurrency" => $"Rejected: streaming concurrency limit reached for model '{modelId}'.",
        "budget_exceeded" => $"Rejected: budget exhausted for model '{modelId}'.",
        "upstream_auth_missing" => $"Upstream auth token not configured for model '{modelId}'.",
        _ => $"Upstream returned {statusCode} for model '{modelId}'.",
    };

    private static string? ResolveApiKeyId(HttpContext context) =>
        context.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value;

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
