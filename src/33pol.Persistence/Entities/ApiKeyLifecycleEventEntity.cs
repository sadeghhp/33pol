using Pol33.Core.Identity;

namespace Pol33.Persistence.Entities;

/// <summary>
/// A row in <c>api_key_lifecycle_events</c>.
/// </summary>
/// <remarks>
/// Deliberately has no foreign key to <c>api_keys</c>: this table must outlive a permanently deleted
/// key, exactly as <c>billing_events</c> already does with its unconstrained <c>ApiKeyId</c>. The
/// prefix and label are snapshots for the same reason — after the key row is gone they are the only
/// way to say which credential the history belongs to.
/// </remarks>
public sealed class ApiKeyLifecycleEventEntity
{
    public Guid Id { get; set; }

    public Guid ApiKeyId { get; set; }

    public Guid TenantId { get; set; }

    public required string KeyPrefix { get; set; }

    public string? Label { get; set; }

    public ApiKeyLifecycleEvent Event { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The admin key that performed the transition, when one was on the request.</summary>
    public Guid? ActorApiKeyId { get; set; }

    /// <summary>Whether the key had recorded usage at the moment of the transition.</summary>
    public bool HadUsage { get; set; }
}
