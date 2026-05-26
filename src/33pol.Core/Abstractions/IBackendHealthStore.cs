using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IBackendHealthStore
{
    bool IsBackendHealthy(string modelId);

    BackendHealth? GetHealth(string modelId);

    IReadOnlyDictionary<string, BackendHealth> GetAllHealth();

    void SetHealth(BackendHealth health);
}
