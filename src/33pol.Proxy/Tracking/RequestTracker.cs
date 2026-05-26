using Pol33.Core.Abstractions;

namespace Pol33.Proxy.Tracking;

public sealed class RequestTracker : IRequestTracker
{
    public IDisposable BeginInferenceRequest(string modelId, bool isStreaming) =>
        NoOpScope.Instance;

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();

        public void Dispose()
        {
        }
    }
}
