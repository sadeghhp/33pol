using Microsoft.Extensions.Caching.Memory;

namespace Pol33.Security.Services;

/// <summary>
/// Remembers, briefly, key hashes that matched no issued key so repeat presentations of the same
/// unknown key are refused without a database round trip.
/// </summary>
/// <remarks>
/// <para>Bounded (<see cref="MaxEntries"/>) and short-lived (<see cref="Ttl"/>) on purpose. The
/// bound means a flood of distinct random keys evicts older entries instead of growing memory
/// without limit; the short TTL means a key issued a moment after being tried is usable within
/// seconds. Only "never issued" outcomes are cached — a revoked or expired key still matches a real
/// row and is not worth remembering, and the success path has its own cache in the validator.</para>
///
/// <para>Keyed by the peppered hash the validator already computes, never by the raw key or its
/// prefix, so a cached negative can never be reached by a different key that merely shares a
/// prefix.</para>
/// </remarks>
public sealed class ApiKeyNegativeCache : IDisposable
{
    public const int MaxEntries = 10_000;

    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>Process-wide instance used when none is supplied through DI.</summary>
    public static ApiKeyNegativeCache Shared { get; } = new();

    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = MaxEntries,
        // Evict a fifth of the entries at a time when full, so a burst of unknown keys does not
        // trigger a compaction per insert.
        CompactionPercentage = 0.2,
    });

    public bool IsKnownInvalid(string keyHash) => _cache.TryGetValue(keyHash, out _);

    public void MarkInvalid(string keyHash) =>
        _cache.Set(keyHash, true, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = Ttl,
        });

    /// <summary>Forgets every remembered negative; used after keys are issued in tests and tooling.</summary>
    public void Clear() => _cache.Clear();

    public void Dispose() => _cache.Dispose();
}
