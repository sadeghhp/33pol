namespace Pol33.Persistence.Entities;

/// <summary>
/// One scoped rate-limit rule: a scope, the thing inside that scope it applies to, and the tier.
/// </summary>
/// <remarks>
/// <para>One table for every optional scope rather than a table each. The scopes differ only in what
/// their key means — a model id, a tenant id, a key id, or a <c>subject|model</c> pair — and share
/// the same three numbers, so separate tables would be five copies of one schema, five repositories
/// and five migrations the next time a tier gains a column.</para>
///
/// <para>The tenant scope's default and per-plan tiers stay where they are, in
/// <c>rate_limit_defaults</c> and <c>rate_limit_plans</c>. Those predate this table, are read on a
/// different path, and moving them would be a data migration with nothing to show for it.</para>
/// </remarks>
public sealed class RateLimitRuleEntity
{
    public Guid Id { get; set; }

    /// <summary>
    /// The scope label — <c>global</c>, <c>tenant</c>, <c>api_key</c>, <c>model</c>,
    /// <c>tenant_model</c>, <c>api_key_model</c> or <c>auth_failure</c>. Stored as text so a row
    /// remains readable in a database browser and an unknown value from a newer build is skipped
    /// rather than silently reinterpreted as a different scope.
    /// </summary>
    public required string Scope { get; set; }

    /// <summary>
    /// What the rule applies to: a model id, tenant id or key id, a <c>subject|model</c> pair, or
    /// <c>*</c> for the scopes that have exactly one partition. Compared case-insensitively, matching
    /// how model ids and tenant slugs are compared everywhere else.
    /// </summary>
    public required string TargetKey { get; set; }

    public int Rpm { get; set; }

    public int Burst { get; set; }

    public int MaxConcurrentStreams { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
