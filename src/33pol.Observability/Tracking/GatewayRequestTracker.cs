using Pol33.Core.Abstractions;
using Pol33.Core.Models.Overview;
using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tracking;

public sealed class GatewayRequestTracker(GatewayRuntimeState runtimeState) : IRequestTracker
{
    public IInferenceRequestScope BeginInferenceRequest(string modelId, bool isStreaming) =>
        BeginInferenceRequest(modelId, isStreaming, tenantId: null);

    public IInferenceRequestScope BeginInferenceRequest(string modelId, bool isStreaming, string? tenantId)
    {
        runtimeState.RecordRequestStart(modelId, isStreaming);
        GatewayMeters.ActiveRequests.Add(1, new KeyValuePair<string, object?>("model", modelId));
        if (isStreaming)
        {
            GatewayMeters.ActiveStreams.Add(1, new KeyValuePair<string, object?>("model", modelId));
        }

        return new InferenceScope(runtimeState, modelId, isStreaming, tenantId);
    }

    public void RecordRejectedRequest(string modelId, string errorCode)
    {
        runtimeState.RecordRequestRejected(modelId, ToReason(errorCode));

        GatewayMeters.InferenceRequests.Add(
            1,
            new KeyValuePair<string, object?>("model", modelId),
            new KeyValuePair<string, object?>("status", "error"));
        GatewayMeters.InferenceErrors.Add(
            1,
            new KeyValuePair<string, object?>("model", modelId),
            new KeyValuePair<string, object?>("code", errorCode));
    }

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
        _ => null,
    };

    private sealed class InferenceScope : IInferenceRequestScope
    {
        private readonly GatewayRuntimeState _runtimeState;
        private readonly string _modelId;
        private readonly bool _isStreaming;
        private readonly string? _tenantId;
        private readonly long _startTimestamp;
        private bool _disposed;
        private bool? _success;
        private bool _canceled;
        private string? _errorCode;

        public InferenceScope(GatewayRuntimeState runtimeState, string modelId, bool isStreaming, string? tenantId)
        {
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
            var success = _success ?? true;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_startTimestamp);

            if (_canceled)
            {
                _runtimeState.RecordRequestCanceled(_modelId, elapsed.TotalMilliseconds, _isStreaming, _tenantId);
                GatewayMeters.InferenceRequests.Add(
                    1,
                    new KeyValuePair<string, object?>("model", _modelId),
                    new KeyValuePair<string, object?>("status", "canceled"));
                GatewayMeters.InferenceDuration.Record(
                    elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("model", _modelId));
                GatewayMeters.ActiveRequests.Add(-1, new KeyValuePair<string, object?>("model", _modelId));
                if (_isStreaming)
                {
                    GatewayMeters.ActiveStreams.Add(-1, new KeyValuePair<string, object?>("model", _modelId));
                }

                return;
            }

            _runtimeState.RecordRequestComplete(_modelId, success, elapsed.TotalMilliseconds, _isStreaming, _tenantId);

            var status = success ? "success" : "error";
            GatewayMeters.InferenceRequests.Add(
                1,
                new KeyValuePair<string, object?>("model", _modelId),
                new KeyValuePair<string, object?>("status", status));

            if (!success)
            {
                GatewayMeters.InferenceErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("model", _modelId),
                    new KeyValuePair<string, object?>("code", _errorCode ?? "unknown"));
            }

            GatewayMeters.InferenceDuration.Record(
                elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("model", _modelId));

            GatewayMeters.ActiveRequests.Add(-1, new KeyValuePair<string, object?>("model", _modelId));

            if (_isStreaming)
            {
                GatewayMeters.ActiveStreams.Add(-1, new KeyValuePair<string, object?>("model", _modelId));
            }
        }
    }
}
