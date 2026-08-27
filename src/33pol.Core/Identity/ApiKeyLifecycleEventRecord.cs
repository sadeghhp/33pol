namespace Pol33.Core.Identity;

/// <summary>
/// One recorded transition. <paramref name="KeyPrefix"/> and <paramref name="Label"/> are snapshots
/// taken when the event occurred, so a deleted key's history still names the credential.
/// </summary>
public sealed record ApiKeyLifecycleEventRecord(
    Guid Id,
    Guid ApiKeyId,
    Guid TenantId,
    string KeyPrefix,
    ApiKeyLifecycleEvent Event,
    DateTimeOffset OccurredAt,
    string? Label = null,
    Guid? ActorApiKeyId = null,
    string? Reason = null,
    bool HadUsage = false);
