namespace Pol33.Core.RateLimiting;

/// <summary>The two shapes a schedule window can take, as stored and transported.</summary>
public static class RateLimitWindowKinds
{
    /// <summary>A single span: from an instant, optionally until another. No <c>Until</c> is an open-ended step change.</summary>
    public const string Once = "once";

    /// <summary>A recurring span: on the listed days of the week, from a local start time to a local end time, in a time zone.</summary>
    public const string Weekly = "weekly";

    public static bool IsKnown(string? kind) =>
        string.Equals(kind, Once, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Weekly, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A window on a scoped rule: a different tier for a span of time. The rule keeps its base tier,
/// which applies whenever no window is active and whenever a window cannot be evaluated.
/// </summary>
/// <param name="Name">Operator-facing label, unique within the rule. Shown in logs, the usage report and the console.</param>
/// <param name="Kind">One of <see cref="RateLimitWindowKinds"/>.</param>
/// <param name="Rpm">The tier in force while the window is active. Same meaning as on the rule.</param>
/// <param name="Burst">Extra tokens above <paramref name="Rpm"/> while active.</param>
/// <param name="MaxConcurrentStreams">Stream cap while active; zero means unlimited.</param>
/// <param name="Suspend">
/// When true the rule enforces nothing while the window is active, as if it did not exist; the tier
/// numbers are ignored. An explicit opt-in so a window with every number at zero is refused rather
/// than silently lifting a limit.
/// </param>
/// <param name="Priority">
/// Optional tie-break. When set, a higher number wins over a lower one regardless of kind; when
/// unset a <c>once</c> window outranks a <c>weekly</c> one.
/// </param>
/// <param name="From">Start instant, <c>once</c> only.</param>
/// <param name="Until">End instant (exclusive), <c>once</c> only; null means open-ended.</param>
/// <param name="Days">Days the window <em>starts</em> on, <c>weekly</c> only: <c>mon</c> … <c>sun</c>.</param>
/// <param name="Start">Local start time, <c>HH:mm</c>, <c>weekly</c> only.</param>
/// <param name="End">
/// Local end time, <c>HH:mm</c> or <c>24:00</c>, <c>weekly</c> only. An end at or before the start
/// means the window runs into the next day.
/// </param>
/// <param name="TimeZone">IANA zone id the local times are read in, <c>weekly</c> only. Null means UTC.</param>
/// <param name="ValidFrom">Optional bound: occurrences that start before this instant do not happen.</param>
/// <param name="ValidUntil">Optional bound: occurrences that start at or after this instant do not happen.</param>
public sealed record RateLimitWindowDefinition(
    string Name,
    string Kind,
    int Rpm,
    int Burst,
    int MaxConcurrentStreams,
    bool Suspend = false,
    int? Priority = null,
    DateTimeOffset? From = null,
    DateTimeOffset? Until = null,
    IReadOnlyList<string>? Days = null,
    string? Start = null,
    string? End = null,
    string? TimeZone = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidUntil = null)
{
    public bool IsOnce => string.Equals(Kind, RateLimitWindowKinds.Once, StringComparison.OrdinalIgnoreCase);

    public bool IsWeekly => string.Equals(Kind, RateLimitWindowKinds.Weekly, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tier this window puts in force; <see cref="RateLimitPolicy.Unlimited"/> when it suspends the rule.</summary>
    public RateLimitPolicy ToPolicy() =>
        Suspend ? RateLimitPolicy.Unlimited : new RateLimitPolicy(Rpm, Burst, MaxConcurrentStreams);

    /// <summary>
    /// Where this window stands when several are active at once. Higher wins. An explicit priority
    /// beats the kind default, so an operator can make a weekly window outrank a one-off when that
    /// is what they mean.
    /// </summary>
    public int Rank => Priority ?? (IsOnce ? 200 : 100);
}
