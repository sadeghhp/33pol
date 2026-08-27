namespace Pol33.Core.Identity;

/// <summary>
/// The single word that names where a key sits in its lifecycle.
/// </summary>
/// <remarks>
/// Lives here rather than on either caller because the admin API and the operator console both
/// render it and must agree: they read from different shapes (a stored record on one side, a
/// response DTO on the other), and two copies of the precedence rules would eventually disagree
/// about a key that is both archived and expired.
/// </remarks>
public static class ApiKeyStatus
{
    public const string Active = "active";
    public const string Revoked = "revoked";
    public const string Archived = "archived";
    public const string Expired = "expired";
    public const string Deleted = "deleted";

    /// <summary>
    /// Archived wins over revoked (an archived key is always revoked, and "archived" is the more
    /// specific fact), and revoked wins over expired: expiry is derived from the clock and would
    /// otherwise mask the deliberate act.
    /// </summary>
    public static string Describe(bool isArchived, bool isRevoked, DateTimeOffset? expiresAt, DateTimeOffset asOf)
    {
        if (isArchived)
        {
            return Archived;
        }

        if (isRevoked)
        {
            return Revoked;
        }

        return expiresAt is { } expiry && expiry <= asOf ? Expired : Active;
    }

    public static string Describe(bool isArchived, bool isRevoked, DateTimeOffset? expiresAt) =>
        Describe(isArchived, isRevoked, expiresAt, DateTimeOffset.UtcNow);
}
