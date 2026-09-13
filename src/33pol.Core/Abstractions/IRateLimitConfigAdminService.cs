using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

public interface IRateLimitConfigAdminService
{
    RateLimitAdminConfig GetCurrent();

    /// <param name="enabled">Global master switch; false disables all rate-limit enforcement.</param>
    /// <param name="adaptiveEnabled">Whether load-aware adaptation may reduce the configured tiers.</param>
    /// <param name="rules">
    /// The complete scoped rule set. Null leaves the stored rules untouched, so a client written
    /// against the older contract — which had no notion of scoped rules — updates the tiers it knows
    /// about without silently deleting rules it cannot see.
    /// </param>
    Task<RateLimitConfigUpdateResult> UpdateAsync(
        bool enabled,
        bool adaptiveEnabled,
        RateLimitTierOptions defaultTier,
        IReadOnlyDictionary<string, RateLimitTierOptions> plans,
        IReadOnlyList<RateLimitRuleDefinition>? rules = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What every rule enforces at <paramref name="at"/>, every window occurrence in
    /// <c>[from, to)</c>, and the moments in that range at which a rule's tier changes. Computed
    /// from the stored configuration; nothing here touches the request path.
    /// </summary>
    RateLimitScheduleReport GetSchedule(DateTimeOffset at, DateTimeOffset from, DateTimeOffset to, int take);

    /// <summary>
    /// How a window an operator is composing would behave, before it is saved: validity, next
    /// occurrence, and how it stands against the rule's other windows.
    /// </summary>
    RateLimitWindowPreview PreviewWindow(RateLimitRuleDefinition rule, string candidateName);
}
