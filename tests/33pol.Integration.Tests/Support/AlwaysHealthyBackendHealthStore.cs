using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Integration.Tests.Support;

internal sealed class AlwaysHealthyBackendHealthStore : IBackendHealthStore
{
    public bool IsBackendHealthy(string modelId) => true;

    public BackendHealth? GetHealth(string modelId) =>
        new(modelId, "http://test", true, 200, null, DateTimeOffset.UtcNow);

    public IReadOnlyDictionary<string, BackendHealth> GetAllHealth() =>
        new Dictionary<string, BackendHealth>();

    public void SetHealth(BackendHealth health)
    {
        // No-op: this test store always reports healthy.
    }
}
