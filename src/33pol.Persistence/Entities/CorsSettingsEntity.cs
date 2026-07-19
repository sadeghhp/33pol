namespace Pol33.Persistence.Entities;

/// <summary>
/// Single-row table holding the CORS allowed-origin list. The origins are stored normalized
/// (as written by the admin service / seed) so the hot path can use them directly.
/// </summary>
public sealed class CorsSettingsEntity
{
    /// <summary>Fixed singleton key (always 1).</summary>
    public int Id { get; set; }

    public List<string> AllowedOrigins { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
