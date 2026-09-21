using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Infrastructure;

namespace Pol33.Persistence.Repositories;

public sealed class RateLimitSettingsRepository(GatewayDbContext dbContext) : IRateLimitSettingsRepository
{
    private const int DefaultsRowId = 1;

    public async Task<long> SaveAsync(
        bool enabled,
        bool adaptiveEnabled,
        RateLimitPolicy defaultTier,
        IReadOnlyDictionary<string, RateLimitPolicy> plans,
        IReadOnlyList<RateLimitRuleDefinition> rules,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaultTier);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(rules);

        long newVersion = 0;

        // The whole read-check-write runs in one BEGIN IMMEDIATE transaction on SQLite, so a second
        // writer blocks on the write lock before it reads the version and then sees the bumped value.
        // The same wrapper the route table uses, for the same reason: both replace their table
        // wholesale, so a write based on a stale read erases the other writer's rows rather than
        // merging with them.
        await GatewayWriteTransaction.RunAsync(
            dbContext,
            async ct =>
            {
                newVersion = await SaveCoreAsync(
                    enabled, adaptiveEnabled, defaultTier, plans, rules, expectedVersion, ct)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return newVersion;
    }

    private async Task<long> SaveCoreAsync(
        bool enabled,
        bool adaptiveEnabled,
        RateLimitPolicy defaultTier,
        IReadOnlyDictionary<string, RateLimitPolicy> plans,
        IReadOnlyList<RateLimitRuleDefinition> rules,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Read and checked before anything is staged, so a conflict costs no work and leaves nothing
        // half-written.
        var version = await dbContext.ConfigVersions
            .FirstOrDefaultAsync(c => c.Id == ConfigVersionRows.RateLimits, cancellationToken)
            .ConfigureAwait(false);

        var currentVersion = version?.Version ?? 0;
        if (expectedVersion is long expected && expected != currentVersion)
        {
            throw new RateLimitVersionConflictException(expected, currentVersion);
        }

        var defaults = await dbContext.RateLimitDefaults
            .FirstOrDefaultAsync(d => d.Id == DefaultsRowId, cancellationToken)
            .ConfigureAwait(false);

        if (defaults is null)
        {
            defaults = new RateLimitDefaultsEntity { Id = DefaultsRowId };
            dbContext.RateLimitDefaults.Add(defaults);
        }

        defaults.Enabled = enabled;
        defaults.AdaptiveEnabled = adaptiveEnabled;
        defaults.Rpm = defaultTier.Rpm;
        defaults.Burst = defaultTier.Burst;
        defaults.MaxConcurrentStreams = defaultTier.MaxConcurrentStreams;
        defaults.UpdatedAt = now;

        // An admin write is itself a definitive rule set, so it closes the one-shot seed window even
        // if the defaults row was created here rather than by the bootstrap. Without it, a restart
        // would seed the appsettings rules on top of what the operator just submitted.
        defaults.RulesSeededAt ??= now;

        // Replace the plan set wholesale (small, bounded). RemoveRange keeps this provider-agnostic
        // (the EF InMemory provider used by tests does not support ExecuteDelete).
        var existingPlans = await dbContext.RateLimitPlans
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.RateLimitPlans.RemoveRange(existingPlans);

        foreach (var (slug, tier) in plans)
        {
            dbContext.RateLimitPlans.Add(new RateLimitPlanEntity
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                Rpm = tier.Rpm,
                Burst = tier.Burst,
                MaxConcurrentStreams = tier.MaxConcurrentStreams,
                UpdatedAt = now,
            });
        }

        // Same wholesale replacement, for the same reason: the rule set an admin submits is the
        // complete one, and merging would leave no way to delete a rule.
        var existingRules = await dbContext.RateLimitRules
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.RateLimitRules.RemoveRange(existingRules);

        foreach (var rule in rules)
        {
            dbContext.RateLimitRules.Add(new RateLimitRuleEntity
            {
                Id = Guid.NewGuid(),
                Scope = rule.Scope,
                TargetKey = rule.TargetKey,
                Rpm = rule.Rpm,
                Burst = rule.Burst,
                MaxConcurrentStreams = rule.MaxConcurrentStreams,
                Enabled = rule.Enabled,
                ScheduleJson = RateLimitScheduleJson.Serialize(rule.Schedule),
                UpdatedAt = now,
            });
        }

        // Bumped in the same SaveChanges so the change and its version signal commit atomically.
        if (version is null)
        {
            version = new ConfigVersionEntity { Id = ConfigVersionRows.RateLimits, Version = currentVersion };
            dbContext.ConfigVersions.Add(version);
        }

        version.Version = currentVersion + 1;
        version.UpdatedAt = now;

        // The general version moves too, but is never compared here. It is the reload signal: the
        // other instances poll it to learn that the snapshot — which carries these rules — is out of
        // date. Only the rate-limit row decides whether this write was based on a stale read.
        var general = await dbContext.ConfigVersions
            .FirstOrDefaultAsync(c => c.Id == ConfigVersionRows.General, cancellationToken)
            .ConfigureAwait(false);
        if (general is null)
        {
            general = new ConfigVersionEntity { Id = ConfigVersionRows.General, Version = 0 };
            dbContext.ConfigVersions.Add(general);
        }

        general.Version += 1;
        general.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return version.Version;
    }
}
