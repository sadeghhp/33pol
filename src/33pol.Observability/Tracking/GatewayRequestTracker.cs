using System.Diagnostics;
using Pol33.Core.Abstractions;
using Pol33.Core.Models.Overview;
using Pol33.Core.Observability;
using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tracking;

public sealed class GatewayRequestTracker(
    GatewayRuntimeState runtimeState,
    MetricCallerBudget? callers = null,
    CallerOpenStreams? openStreams = null) : IRequestTracker
{
    private readonly MetricCallerBudget _callers = callers ?? new MetricCallerBudget();
    private readonly CallerOpenStreams _openStreams = openStreams ?? new CallerOpenStreams();

    public IInferenceRequestScope BeginInferenceRequest(string modelId, bool isStreaming) =>
        BeginInferenceRequest(modelId, isStreaming, tenantId: null);

    public IInferenceRequestScope BeginInferenceRequest(string modelId, bool isStreaming, string? tenantId) =>
        BeginInferenceRequest(modelId, isStreaming, tenantId, MetricCaller.Anonymous);

    public IInferenceRequestScope BeginInferenceRequest(
        string modelId,
        bool isStreaming,
        string? tenantId,
        MetricCaller caller)
    {
        runtimeState.RecordRequestStart(modelId, isStreaming);
        GatewayMeters.ActiveRequests.Add(1, new KeyValuePair<string, object?>("model", modelId));
        if (isStreaming)
        {
            GatewayMeters.ActiveStreams.Add(1, new KeyValuePair<string, object?>("model", modelId));
        }

        var resolved = _callers.Resolve(caller);
        CallerOpenStreams.Count? callerStreams = null;
        if (isStreaming)
        {
            callerStreams = _openStreams.For(resolved);
            callerStreams.StreamStarted();
        }

        return new InferenceScope(runtimeState, modelId, isStreaming, tenantId, resolved, callerStreams);
    }

    public void RecordRejectedRequest(string modelId, string errorCode) =>
        RecordRejectedRequest(modelId, errorCode, MetricCaller.Anonymous);

    public void RecordRejectedRequest(string modelId, string errorCode, MetricCaller caller)
    {
        runtimeState.RecordRequestRejected(modelId, ToReason(errorCode));

        var resolved = _callers.Resolve(caller);
        GatewayMeters.InferenceRequests.Add(1, RequestTags(modelId, "error", resolved));
        GatewayMeters.InferenceErrors.Add(1, ErrorTags(modelId, errorCode, resolved));
    }

    // The active-request and active-stream gauges stay on the model alone. Open streams per caller
    // are a gauge of their own, gateway_key_open_streams, kept by CallerOpenStreams.
    private static TagList RequestTags(string modelId, string status, MetricCaller caller) =>
        new()
        {
            { "model", modelId },
            { "status", status },
            { "tenant", caller.Tenant },
            { "key", caller.Key },
        };

    private static TagList ErrorTags(string modelId, string code, MetricCaller caller) =>
        new()
        {
            { "model", modelId },
            { "code", code },
            { "tenant", caller.Tenant },
            { "key", caller.Key },
        };

    private static TagList DurationTags(string modelId, MetricCaller caller) =>
        new()
        {
            { "model", modelId },
            { "tenant", caller.Tenant },
            { "key", caller.Key },
        };

    /// <summary>
    /// Admission outcomes the router reports, as windowed reasons. Stream concurrency is null here
    /// because the router already counted it through <c>RecordRateLimitRejection</c>.
    /// </summary>
    private static RejectionReason? ToReason(string outcome) => outcome switch
    {
        "bulkhead_full" => RejectionReason.Bulkhead,
        "backend_unhealthy" => RejectionReason.BackendUnhealthy,
        "circuit_open" => RejectionReason.CircuitOpen,
        "insufficient_scope" => RejectionReason.GrantDenied,
        "model_stopped" => RejectionReason.ModelStopped,
        _ => null,
    };

    private sealed class InferenceScope : IInferenceRequestScope
    {
        private readonly GatewayRuntimeState _runtimeState;
        private readonly string _modelId;
        private readonly bool _isStreaming;
        private readonly string? _tenantId;
        private readonly MetricCaller _caller;
        private readonly CallerOpenStreams.Count? _callerStreams;
        private readonly long _startTimestamp;
        private bool _disposed;
        private bool? _success;
        private bool _canceled;
        private string? _errorCode;

        public InferenceScope(
            GatewayRuntimeState runtimeState,
            string modelId,
            bool isStreaming,
            string? tenantId,
            MetricCaller caller,
            CallerOpenStreams.Count? callerStreams)
        {
            _caller = caller;
            _callerStreams = callerStreams;
            _runtimeState = runtimeState;
            _modelId = modelId;
            _isStreaming = isStreaming;
            _tenantId = tenantId;
            _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public void SetOutcome(bool success, string? errorCode = null)
        {
            _success = success;
            _errorCode = errorCode;
            _canceled = false;
        }

        public void SetClientCanceled()
        {
            _success = false;
            _errorCode = "client_canceled";
            _canceled = true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _callerStreams?.StreamEnded();
            var success = _success ?? true;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_startTimestamp);

            if (_canceled)
            {
                _runtimeState.RecordRequestCanceled(_modelId, elapsed.TotalMilliseconds, _isStreaming, _tenantId);
                GatewayMeters.InferenceRequests.Add(1, RequestTags(_modelId, "canceled", _caller));
                GatewayMeters.InferenceDuration.Record(elapsed.TotalSeconds, DurationTags(_modelId, _caller));
                GatewayMeters.ActiveRequests.Add(-1, new KeyValuePair<string, object?>("model", _modelId));
                if (_isStreaming)
                {
                    GatewayMeters.ActiveStreams.Add(-1, new KeyValuePair<string, object?>("model", _modelId));
                }

                return;
            }

            _runtimeState.RecordRequestComplete(_modelId, success, elapsed.TotalMilliseconds, _isStreaming, _tenantId);

            var status = success ? "success" : "error";
            GatewayMeters.InferenceRequests.Add(1, RequestTags(_modelId, status, _caller));

            if (!success)
            {
                GatewayMeters.InferenceErrors.Add(1, ErrorTags(_modelId, _errorCode ?? "unknown", _caller));
            }

            GatewayMeters.InferenceDuration.Record(elapsed.TotalSeconds, DurationTags(_modelId, _caller));

            GatewayMeters.ActiveRequests.Add(-1, new KeyValuePair<string, object?>("model", _modelId));

            if (_isStreaming)
            {
                GatewayMeters.ActiveStreams.Add(-1, new KeyValuePair<string, object?>("model", _modelId));
            }
        }
    }
}
