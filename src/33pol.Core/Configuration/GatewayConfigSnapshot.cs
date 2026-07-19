namespace Pol33.Core.Configuration;

/// <summary>
/// Immutable aggregate of all database-backed operational configuration, delivered to the request
/// hot path via <see cref="Pol33.Core.Abstractions.IGatewayConfigProvider"/>. It grows one section
/// per migrated config area (CORS, rate limits, model routes, quota); this initial version carries
/// only the config <see cref="Version"/>.
///
/// <para><see cref="Version"/> is the monotonic config version stored in the database. The snapshot
/// syncer polls it to detect out-of-band changes and an admin write bumps it so a direct in-process
/// refresh can be confirmed. Init-only members keep adding sections a non-breaking change.</para>
/// </summary>
public sealed record GatewayConfigSnapshot
{
    public long Version { get; init; }

    /// <summary>The safe, hardcoded configuration used before the first successful database load.</summary>
    public static GatewayConfigSnapshot Defaults { get; } = new();
}
