namespace Pol33.Core.Abstractions;

/// <summary>
/// Reads and writes the database-backed CORS allowed-origin list. Registered only when a database
/// connection string is configured.
/// </summary>
public interface ICorsSettingsRepository
{
    /// <summary>Returns the persisted allowed origins, or <c>null</c> if no row has been written yet.</summary>
    Task<IReadOnlyList<string>?> GetAllowedOriginsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the allowed-origin list and bumps the config version in a single atomic write, so the
    /// change and its version signal commit together.
    /// </summary>
    Task SaveAllowedOriginsAsync(
        IReadOnlyList<string> allowedOrigins,
        CancellationToken cancellationToken = default);
}
