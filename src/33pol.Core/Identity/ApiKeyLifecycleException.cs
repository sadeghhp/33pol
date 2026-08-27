namespace Pol33.Core.Identity;

/// <summary>
/// A lifecycle transition the caller asked for that the key's current state does not allow —
/// archiving a key that is still live, deleting one that has served traffic, and so on.
/// </summary>
/// <remarks>
/// Carries a stable machine-readable <see cref="Code"/> so endpoints can put it in the response body
/// and the admin console can branch on it. Distinct from <see cref="InvalidOperationException"/>,
/// which the key endpoints already map to 400: these are conflicts with stored state, not malformed
/// input, and they are expected outcomes rather than faults.
/// </remarks>
public sealed class ApiKeyLifecycleException : Exception
{
    public ApiKeyLifecycleException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; }

    /// <summary>Billing events recorded against the key, when the conflict is <c>key_has_usage</c>.</summary>
    public int BillingEventCount { get; init; }

    /// <summary>When the key last authenticated, when the conflict is <c>key_has_usage</c>.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    public static ApiKeyLifecycleException NotRevoked(string action) =>
        new("key_not_revoked", $"The key must be revoked before it can be {action}.");

    public static ApiKeyLifecycleException AlreadyArchived() =>
        new("already_archived", "The key is already archived.");

    public static ApiKeyLifecycleException NotArchived() =>
        new("not_archived", "The key is not archived.");

    public static ApiKeyLifecycleException HasUsage(int billingEventCount, DateTimeOffset? lastUsedAt) =>
        new(
            "key_has_usage",
            "This key has recorded usage and cannot be permanently deleted. Archive it instead to keep its usage history.")
        {
            BillingEventCount = billingEventCount,
            LastUsedAt = lastUsedAt,
        };

    public static ApiKeyLifecycleException SelfAction(string action) =>
        new("self_action", $"A key cannot {action} itself. Use a different admin key.");

    public static ApiKeyLifecycleException LastAdminKey() =>
        new(
            "last_admin_key",
            "This is the tenant's only active admin key. Create a replacement before revoking it.");
}
