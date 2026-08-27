namespace Pol33.Core.Identity;

/// <summary>
/// A transition in an API key's life, recorded in <c>api_key_lifecycle_events</c>.
/// </summary>
/// <remarks>
/// These rows outlive the key: a permanently deleted key leaves its <see cref="Deleted"/> tombstone
/// behind so the history of a credential that once existed remains answerable. That is why the
/// lifecycle table carries snapshots of the prefix and label rather than a foreign key.
/// </remarks>
public enum ApiKeyLifecycleEvent
{
    Created,
    Revoked,
    Archived,
    Unarchived,
    Deleted,
}
