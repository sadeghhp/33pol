using Microsoft.Extensions.Caching.Memory;
using Pol33.Core.Abstractions;

namespace Pol33.Security.Services;

public sealed class DebouncedApiKeyLastUsedTracker : IApiKeyLastUsedTracker
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMinutes(5);

    private readonly IApiKeyRepository _apiKeys;
    private readonly IMemoryCache _cache;

    public DebouncedApiKeyLastUsedTracker(IApiKeyRepository apiKeys, IMemoryCache cache)
    {
        _apiKeys = apiKeys;
        _cache = cache;
    }

    public async ValueTask TouchAsync(Guid apiKeyId, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"api-key-last-used-touch:{apiKeyId:N}";
        if (_cache.TryGetValue(cacheKey, out _))
        {
            return;
        }

        _cache.Set(cacheKey, true, DebounceInterval);
        await _apiKeys.TouchLastUsedAsync(apiKeyId, atUtc, cancellationToken).ConfigureAwait(false);
    }
}
