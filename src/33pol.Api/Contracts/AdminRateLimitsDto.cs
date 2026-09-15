using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Api.Contracts;

/// <summary>
/// GET response and PUT request body for <c>/admin/api/rate-limits</c>.
/// </summary>
public sealed class AdminRateLimitsDto
{
    /// <summary>
    /// Global master switch. When false the gateway enforces no request-rate or stream-concurrency
    /// limits. Tier values below are still persisted so the configuration survives a disable/enable cycle.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether load-aware adaptation may hold model limits below their configured rate while a model
    /// is saturated. Never raises a limit; see the adaptive section of the usage report for what it
    /// is currently doing and why.
    /// </summary>
    public bool AdaptiveEnabled { get; set; }

    public RateLimitTierOptions Default { get; set; } = new();

    public Dictionary<string, RateLimitTierOptions> Plans { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The scoped rules: per-model, per-key, combined, and the global, auth-failure and anonymous
    /// singletons.
    /// </summary>
    /// <remarks>
    /// Null on a PUT means "leave the stored rules alone", which is what a client written against
    /// the older contract sends. An empty array means "there are no rules" and does delete them, so
    /// the two are deliberately different: a client that cannot see rules must not be able to
    /// destroy them by omission.
    /// </remarks>
    public List<AdminRateLimitRuleDto>? Rules { get; set; }

    /// <summary>
    /// The configuration version this body was read at. Response-only; ignored on a PUT.
    /// </summary>
    /// <remarks>
    /// The same value as the <c>ETag</c> on the GET, carried in the body as well so a browser client
    /// can read it without exposing response headers to script. The write precondition is the
    /// <c>If-Match</c> header alone — a version echoed in a request body is not a precondition, it is
    /// just another field the caller controls.
    /// </remarks>
    public long Version { get; set; }
}

/// <param name="Scope">
/// <c>global</c>, <c>tenant</c>, <c>api_key</c>, <c>model</c>, <c>tenant_model</c>,
/// <c>api_key_model</c>, <c>auth_failure</c> or <c>anonymous</c>.
/// </param>
/// <param name="Target">
/// What the rule applies to: an id for the single-subject scopes, <c>subject|model</c> for the
/// combined ones, and <c>*</c> for the scopes with one partition.
/// </param>
/// <param name="Rpm">Sustained requests per minute; zero limits only concurrency.</param>
/// <param name="Burst">Extra requests an idle partition may spend at once.</param>
/// <param name="MaxConcurrentStreams">Concurrent streaming responses; zero means unlimited.</param>
public sealed record AdminRateLimitRuleDto(
    string Scope,
    string Target,
    int Rpm,
    int Burst,
    int MaxConcurrentStreams)
{
    /// <summary>
    /// The rule's schedule windows. Null on a PUT means "keep the windows stored for this rule";
    /// an empty array removes them. Always present on a GET.
    /// </summary>
    public List<AdminRateLimitWindowDto>? Schedule { get; init; }

    /// <summary>
    /// Whether the rule is enforced. A disabled rule keeps its tier and windows and is still returned
    /// by the GET; it simply enforces nothing.
    /// </summary>
    /// <remarks>
    /// Defaults to true, so a client that predates the field keeps writing enforced rules rather than
    /// silently switching off everything it round-trips.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <remarks>
    /// The scope is canonicalised rather than merely trimmed. It is persisted verbatim and compared
    /// against the canonical constants everywhere else, so a spelling like <c>"Anonymous"</c> used to
    /// be stored as written and then never match the singleton it names.
    /// </remarks>
    public RateLimitRuleDefinition ToDefinition() =>
        new(RateLimitScopeNames.Canonical(Scope), Target ?? string.Empty, Rpm, Burst, MaxConcurrentStreams)
        {
            Schedule = Schedule?.Select(static w => w.ToDefinition()).ToArray(),
            Enabled = Enabled,
        };

    public static AdminRateLimitRuleDto FromDefinition(RateLimitRuleDefinition rule) =>
        new(rule.Scope, rule.TargetKey, rule.Rpm, rule.Burst, rule.MaxConcurrentStreams)
        {
            Schedule = rule.Windows.Select(AdminRateLimitWindowDto.FromDefinition).ToList(),
            Enabled = rule.Enabled,
        };
}

/// <summary>
/// One schedule window on a rule. <c>once</c> windows use <paramref name="From"/> and
/// <paramref name="Until"/>; <c>weekly</c> windows use <paramref name="Days"/>,
/// <paramref name="Start"/>, <paramref name="End"/> and <paramref name="TimeZone"/>.
/// </summary>
public sealed record AdminRateLimitWindowDto(
    string Name,
    string Kind,
    int Rpm,
    int Burst,
    int MaxConcurrentStreams,
    bool Suspend = false,
    int? Priority = null,
    DateTimeOffset? From = null,
    DateTimeOffset? Until = null,
    List<string>? Days = null,
    string? Start = null,
    string? End = null,
    string? TimeZone = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidUntil = null)
{
    public RateLimitWindowDefinition ToDefinition() =>
        new(
            Name?.Trim() ?? string.Empty,
            Kind?.Trim().ToLowerInvariant() ?? string.Empty,
            Rpm,
            Burst,
            MaxConcurrentStreams,
            Suspend,
            Priority,
            From,
            Until,
            Days?.Select(static d => d?.Trim().ToLowerInvariant() ?? string.Empty).ToArray(),
            Start?.Trim(),
            End?.Trim(),
            string.IsNullOrWhiteSpace(TimeZone) ? null : TimeZone.Trim(),
            ValidFrom,
            ValidUntil);

    public static AdminRateLimitWindowDto FromDefinition(RateLimitWindowDefinition w) =>
        new(
            w.Name,
            w.Kind,
            w.Rpm,
            w.Burst,
            w.MaxConcurrentStreams,
            w.Suspend,
            w.Priority,
            w.From,
            w.Until,
            w.Days?.ToList(),
            w.Start,
            w.End,
            w.TimeZone,
            w.ValidFrom,
            w.ValidUntil);
}

/// <summary>
/// POST body for <c>/admin/api/rate-limits/windows/preview</c>: the rule as it would be with the
/// candidate window included, and which window is being composed.
/// </summary>
public sealed class AdminRateLimitWindowPreviewDto
{
    public string Scope { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public int Rpm { get; set; }

    public int Burst { get; set; }

    public int MaxConcurrentStreams { get; set; }

    public List<AdminRateLimitWindowDto> Windows { get; set; } = [];

    public string Candidate { get; set; } = string.Empty;
}

/// <summary>
/// POST body for <c>/admin/api/rate-limits/schedule/preview</c>: a candidate rule set the console
/// has staged but not saved, and the same instant/range parameters the GET takes as query string.
/// </summary>
/// <remarks>
/// The GET answers for what is stored, which is the wrong question while an operator is composing a
/// change — the calendar they are checking is the one they are about to save, not the one already
/// deployed. Nothing here is persisted; the rules travel in the body because a staged set is far
/// too large for a query string.
/// </remarks>
public sealed class AdminRateLimitSchedulePreviewDto
{
    /// <summary>The staged rule set, complete. Unlike the PUT, null is not "leave the stored ones alone" — there is nothing to leave alone in a preview, so it reads as an empty set.</summary>
    public List<AdminRateLimitRuleDto>? Rules { get; set; }

    public DateTimeOffset? At { get; set; }

    /// <summary>The same wall-clock spelling the GET accepts, resolved in <c>timeZone</c>.</summary>
    public string? AtLocal { get; set; }

    public string? TimeZone { get; set; }

    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }

    public int? Take { get; set; }
}
