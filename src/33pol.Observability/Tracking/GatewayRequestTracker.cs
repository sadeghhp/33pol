using Pol33.Core.Abstractions;
using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tracking;

public sealed class GatewayRequestTracker(GatewayRuntimeState runtimeState) : IRequestTracker
{
    public IInferenceRequestScope BeginInferenceRequest(string modelId, bool isStreaming)
    {
        runtimeState.RecordRequestStart(modelId, isStreaming);
        GatewayMeters.ActiveRequests.Add(1, new KeyValuePair<string, object?>("model", modelId));
        if (isStreaming)
        {
            GatewayMeters.ActiveStreams.Add(1, new KeyValuePair<string, object?>("model", modelId));
        }

        return new InferenceScope(runtimeState, modelId, isStreaming);
    }

    public void RecordRejectedRequest(string modelId, string errorCode)
    {
        runtimeState.RecordRequestRejected(modelId);

        GatewayMeters.InferenceRequests.Add(
            1,
            new KeyValuePair<string, object?>("model", modelId),
            new KeyValuePair<string, object?>("status", "error"));
        GatewayMeters.InferenceErrors.Add(
            1,
            new KeyValuePair<string, object?>("model", modelId),
            new KeyValuePair<string, object?>("code", errorCode));
    }

    private sealed class InferenceScope : IInferenceRequestScope
    {
        private readonly GatewayRuntimeState _runtimeState;
        private readonly string _modelId;
        private readonly bool _isStreaming;
        private readonly long _startTimestamp;
        private bool _disposed;
        private bool? _success;
        private string? _errorCode;

        public InferenceScope(GatewayRuntimeState runtimeState, string modelId, bool isStreaming)
        {
            _runtimeState = runtimeState;
            _modelId = modelId;
            _isStreaming = isStreaming;
            _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public void SetOutcome(bool success, string? errorCode = null)
        {
            _success = success;
            _errorCode = errorCode;
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
            _runtimeState.RecordRequestComplete(_modelId, success, elapsed.TotalMilliseconds, _isStreaming);

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
