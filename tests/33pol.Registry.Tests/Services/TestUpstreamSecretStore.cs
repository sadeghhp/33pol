using Pol33.Core.Abstractions;

namespace Pol33.Registry.Tests.Services;

internal sealed class TestUpstreamSecretStore : IUpstreamSecretStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string modelId, out string? secret) =>
        _secrets.TryGetValue(modelId.Trim(), out secret);

    public Task PutAsync(string modelId, string secret, CancellationToken cancellationToken = default)
    {
        _secrets[modelId.Trim()] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string modelId, CancellationToken cancellationToken = default)
    {
        _secrets.Remove(modelId.Trim());
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string modelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_secrets.ContainsKey(modelId.Trim()));
}
