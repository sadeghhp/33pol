using Pol33.Core.Abstractions;
using Pol33.Core.RateLimiting;

namespace Pol33.Policy.RateLimiting;

/// <param name="configProvider">The live configuration snapshot.</param>
/// <param name="authState">
/// Whether the gateway requires credentials. The anonymous tier only means something where a caller
/// could have presented a key and did not; with authentication off every caller has no tenant, and
/// the default tier is theirs exactly as before. Optional so hand-built resolvers keep compiling;
/// absent means "authentication not required".
/// </param>
public sealed class RateLimitPolicyResolver(
    IGatewayConfigProvider configProvider,
    IGatewayAuthenticationState? authState = null) : IRateLimitPolicyResolver
{
    public bool IsEnabled() => configProvider.Current.RateLimits.Enabled;

    public RateLimitPolicy Resolve(string? planSlug, string? tenantId, string? tenantSlug) =>
        ResolveTenantTier(configProvider.Current.RateLimits, planSlug, tenantId, tenantSlug);

    public RateLimitPolicy ResolveAuthFailure() =>
        ResolveAuthFailureTier(configProvider.Current.RateLimits);

    public RateLimitPolicy ResolveAnonymous() =>
        ResolveAnonymousTier(configProvider.Current.RateLimits, authState?.IsAuthenticationRequired ?? false);

    /// <summary>
    /// The tier applied to a tenant, in the one place precedence exists: a per-tenant override wins
    /// over the tenant's plan, which wins over the default.
    /// </summary>
    /// <remarks>
    /// <para>Shared with <see cref="RateLimitPlanResolver"/> so the tenant scope of a rule set and
    /// the standalone tier lookup can never drift apart — two implementations of the same precedence
    /// would eventually disagree, and the symptom would be one middleware admitting what the next
    /// one refuses.</para>
    ///
    /// <para>A <c>tenant</c> rule may be written against the tenant id or its slug. The id is what
    /// the request carries and what the bucket is keyed on, so it is tried first; but an operator
    /// knows the customer by its slug, and a rule written that way was previously accepted,
    /// persisted, shown in the admin UI, and then silently never matched anything. Accepting both is
    /// what makes the configuration mean what it looks like it means.</para>
    ///
    /// <para>An override with a zero rpm does not replace the rate. Every other scoped rule reads a
    /// zero rpm as "this rule does not limit the rate", and the runbook says so; flooring the
    /// override to 1 rpm instead turned a rule meant to cap a tenant's streams into a
    /// one-request-per-minute limit on that tenant. Such an override keeps the plan or default
    /// rate and contributes only its stream cap — see <see cref="Compose"/>.</para>
    /// </remarks>
    internal static RateLimitPolicy ResolveTenantTier(
        Core.Configuration.RateLimitsConfigSection rateLimits,
        string? planSlug,
        string? tenantId,
        string? tenantSlug)
    {
        var baseTier =
            !string.IsNullOrWhiteSpace(planSlug) && rateLimits.Plans.TryGetValue(planSlug, out var planTier)
                ? Clamp(planTier)
                : Clamp(rateLimits.Default);

        if (TryResolveTenantOverride(rateLimits, tenantId, out var tenantTier) ||
            TryResolveTenantOverride(rateLimits, tenantSlug, out tenantTier))
        {
            return Compose(baseTier, tenantTier);
        }

        return baseTier;
    }

    /// <summary>
    /// Which configured control supplies the tenant scope's rate and which supplies its stream cap,
    /// for the per-limit usage report. Mirrors <see cref="ResolveTenantTier"/> and
    /// <see cref="Compose"/> exactly: an override with a rate owns both, one without keeps the base
    /// tier's rate and owns only the cap.
    /// </summary>
    internal static (string RateLimitId, string StreamLimitId) ResolveTenantTierSource(
        Core.Configuration.RateLimitsConfigSection rateLimits,
        string? planSlug,
        string? tenantId,
        string? tenantSlug)
    {
        var baseId = !string.IsNullOrWhiteSpace(planSlug) && rateLimits.Plans.ContainsKey(planSlug)
            ? RateLimitLimitIds.Plan(planSlug)
            : RateLimitLimitIds.Default;

        string? matched = null;
        if (TryResolveTenantOverride(rateLimits, tenantId, out var tier))
        {
            matched = tenantId;
        }
        else if (TryResolveTenantOverride(rateLimits, tenantSlug, out tier))
        {
            matched = tenantSlug;
        }

        if (matched is null)
        {
            return (baseId, baseId);
        }

        var overrideId = RateLimitLimitIds.Rule(RateLimitScopeNames.Tenant, matched);
        return (tier.Rpm > 0 ? overrideId : baseId, overrideId);
    }

    /// <summary>The anonymous counterpart of <see cref="ResolveTenantTierSource"/>.</summary>
    internal static (string RateLimitId, string StreamLimitId) ResolveAnonymousTierSource(
        Core.Configuration.RateLimitsConfigSection rateLimits,
        bool authenticationRequired)
    {
        if (!authenticationRequired || rateLimits.Anonymous.EnforcesNothing)
        {
            return (RateLimitLimitIds.Default, RateLimitLimitIds.Default);
        }

        return (
            rateLimits.Anonymous.Rpm > 0 ? RateLimitLimitIds.Anonymous : RateLimitLimitIds.Default,
            RateLimitLimitIds.Anonymous);
    }

    private static bool TryResolveTenantOverride(
        Core.Configuration.RateLimitsConfigSection rateLimits,
        string? target,
        out RateLimitPolicy tier)
    {
        if (!string.IsNullOrWhiteSpace(target))
        {
            return rateLimits.TenantOverrides.TryGetValue(target, out tier!);
        }

        tier = default!;
        return false;
    }

    /// <summary>
    /// The tier for requests authentication refuses. Falls back to the default tier when no
    /// auth-failure tier is configured, which is what deployments that predate the setting get.
    /// </summary>
    internal static RateLimitPolicy ResolveAuthFailureTier(Core.Configuration.RateLimitsConfigSection rateLimits) =>
        rateLimits.AuthFailure.EnforcesRate
            ? Clamp(rateLimits.AuthFailure)
            : Clamp(rateLimits.Default);

    /// <summary>
    /// The tier for callers with no credential. Falls back to the default tier when no anonymous
    /// tier is configured, and composes with it when the anonymous tier caps streams only.
    /// </summary>
    /// <param name="authenticationRequired">
    /// False when the gateway accepts every caller without a key. Then nobody is "anonymous" in the
    /// sense the tier exists for — there was no credential to leave out — and the default tier
    /// applies, which is what such deployments have always enforced.
    /// </param>
    internal static RateLimitPolicy ResolveAnonymousTier(
        Core.Configuration.RateLimitsConfigSection rateLimits,
        bool authenticationRequired) =>
        !authenticationRequired || rateLimits.Anonymous.EnforcesNothing
            ? Clamp(rateLimits.Default)
            : Compose(Clamp(rateLimits.Default), rateLimits.Anonymous);

    /// <summary>
    /// A tier assembled from a base tier and an override that may leave the rate alone.
    /// </summary>
    /// <remarks>
    /// This is the only place a tier is built from two sources. An override with a positive rpm is
    /// the whole tier, as before. An override with a zero rpm keeps the base tier's rate — rpm and
    /// burst together, because a burst without a rate to refill it is not a meaningful budget, and
    /// validation requires it to be zero — and contributes only its stream cap, so "cap this
    /// tenant's streams, leave its rate to the plan" is expressible without restating the plan's
    /// numbers in every override.
    /// </remarks>
    internal static RateLimitPolicy Compose(RateLimitPolicy baseTier, RateLimitPolicy overrideTier) =>
        overrideTier.Rpm > 0
            ? Clamp(overrideTier)
            : baseTier with { MaxConcurrentStreams = Math.Max(0, overrideTier.MaxConcurrentStreams) };

    /// <summary>
    /// Floors the default and plan tiers at 1 rpm. Zero is the "this scope does not limit the
    /// rate" value for the optional scopes, but the tenant scope is the gateway's only universal
    /// limit — reading a zero there as "unlimited" would turn a misconfiguration into no enforcement
    /// at all, silently.
    /// </summary>
    private static RateLimitPolicy Clamp(RateLimitPolicy tier) =>
        new(
            Math.Max(1, tier.Rpm),
            Math.Max(0, tier.Burst),
            Math.Max(0, tier.MaxConcurrentStreams));
}
