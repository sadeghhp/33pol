namespace Pol33.Core.Abstractions;

public interface IRequestTracker
{
    /// <summary>
    /// Called when an inference request is about to be forwarded (Phase 4 metrics hook).
    /// </summary>
    IDisposable BeginInferenceRequest(string modelId, bool isStreaming);
}
