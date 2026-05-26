using Pol33.Core.Abstractions;
using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tracking;

public sealed class GatewayRequestTracker(GatewayRuntimeState runtimeState) : IRequestTracker
{
    public IDisposable BeginInferenceRequest(string modelId, bool isStreaming)
    {
        runtimeState.RecordRequestStart(isStreaming);
        if (isStreaming)
        {
            GatewayMeters.ActiveStreams.Add(1, new KeyValuePair<string, object?>("model", modelId));
        }

        return new InferenceScope(runtimeState, modelId, isStreaming);
    }

    private sealed class InferenceScope : IDisposable
    {
        private readonly GatewayRuntimeState _runtimeState;
        private readonly string _modelId;
        private readonly bool _isStreaming;
        private readonly long _startTimestamp;
        private bool _disposed;

        public InferenceScope(GatewayRuntimeState runtimeState, string modelId, bool isStreaming)
        {
            _runtimeState = runtimeState;
            _modelId = modelId;
            _isStreaming = isStreaming;
            _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_startTimestamp);
            var success = true;
            _runtimeState.RecordRequestComplete(_modelId, success, elapsed.TotalMilliseconds, _isStreaming);

            GatewayMeters.InferenceRequests.Add(
                1,
                new KeyValuePair<string, object?>("model", _modelId),
                new KeyValuePair<string, object?>("status", "success"));
            GatewayMeters.InferenceDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("model", _modelId));

            if (_isStreaming)
            {
                GatewayMeters.ActiveStreams.Add(-1, new KeyValuePair<string, object?>("model", _modelId));
            }
        }
    }
}
