using Pol33.Core.Abstractions;

namespace Pol33.Proxy.Tracking;

public sealed class RequestTracker : IRequestTracker
{
    public IInferenceRequestScope BeginInferenceRequest(string modelId, bool isStreaming) =>
        NoOpScope.Instance;

    private sealed class NoOpScope : IInferenceRequestScope
    {
        public static readonly NoOpScope Instance = new();

        public void SetOutcome(bool success, string? errorCode = null)
        {
        }

        public void Dispose()
        {
        }
    }
}
