namespace Pol33.Core.Identity;

/// <param name="UpdateExpiry">
/// When false, <paramref name="ExpiresAt"/> is ignored and the stored expiry is left untouched, so a
/// metadata-only edit cannot silently clear an expiry by omitting the field.
/// </param>
public sealed record ApiKeyMetadataUpdate(
    string? Label,
    string? Assignee,
    string? Description,
    string? CostCenter,
    DateTimeOffset? ExpiresAt = null,
    bool UpdateExpiry = false);
