using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Persistence.Bootstrap;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Bootstrap;

/// <summary>
/// The scoped rules an operator writes in appsettings have to reach the database, because the
/// database — not appsettings — is what the live config snapshot is loaded from on a deployment
/// that has one.
/// </summary>
/// <remarks>
/// Every one of these rules was accepted by configuration binding, reported in the "seeded
/// rate-limit settings" log line, documented in the runbook, and then silently dropped: only the
/// default tier and the plan tiers were written, so <c>Models</c>, <c>ApiKeys</c>,
/// <c>TenantModels</c>, <c>ApiKeyModels</c>, <c>Tenants</c>, <c>Global</c> and <c>AuthFailure</c>
/// were configuration with no effect at all.
/// </remarks>
public sealed class GatewayDbBootstrapRateLimitSeedTests
{
    [Fact]
    public async Task EnsureInitializedAsync_SeedsEveryConfiguredScopeIntoTheRulesTable()
    {
        await using var db = CreateDb(nameof(EnsureInitializedAsync_SeedsEveryConfiguredScopeIntoTheRulesTable));

        await CreateBootstrap(db, ConfiguredScopes()).EnsureInitializedAsync();

        var rules = await db.RateLimitRules.AsNoTracking().ToListAsync();

        rules.Select(r => (r.Scope, r.TargetKey)).Should().BeEquivalentTo(new[]
        {
            (RateLimitScopeNames.Global, "*"),
            (RateLimitScopeNames.Tenant, "acme"),
            (RateLimitScopeNames.ApiKey, "key-1"),
            (RateLimitScopeNames.Model, "local-mock"),
            (RateLimitScopeNames.TenantModel, "acme|local-mock"),
            (RateLimitScopeNames.ApiKeyModel, "key-1|local-mock"),
            (RateLimitScopeNames.AuthFailure, "*"),
        });

        var model = rules.Single(r => r.Scope == RateLimitScopeNames.Model);
        (model.Rpm, model.Burst, model.MaxConcurrentStreams).Should().Be((7, 2, 3));
    }

    /// <summary>
    /// The adaptive switch lives on the defaults row, which the snapshot is loaded from, so leaving
    /// it at its column default made <c>RateLimiting:Adaptive:Enabled</c> another setting with no
    /// effect on a database-backed deployment.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_CarriesTheAdaptiveSwitchOntoTheDefaultsRow()
    {
        await using var db = CreateDb(nameof(EnsureInitializedAsync_CarriesTheAdaptiveSwitchOntoTheDefaultsRow));

        var options = new RateLimitingOptions();
        options.Adaptive.Enabled = true;

        await CreateBootstrap(db, options).EnsureInitializedAsync();

        var defaults = await db.RateLimitDefaults.AsNoTracking().SingleAsync();
        defaults.AdaptiveEnabled.Should().BeTrue();
        defaults.RulesSeededAt.Should().NotBeNull("the seed is stamped so it never runs twice");
    }

    /// <summary>
    /// Seeding is a one-shot, not "top up whatever is missing". An operator who deletes every rule
    /// through the admin API has made a configuration decision, and a restart must not quietly undo
    /// it by restoring the appsettings set.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_AfterAnOperatorDeletedEveryRule_DoesNotReseedThem()
    {
        await using var db = CreateDb(nameof(EnsureInitializedAsync_AfterAnOperatorDeletedEveryRule_DoesNotReseedThem));

        await CreateBootstrap(db, ConfiguredScopes()).EnsureInitializedAsync();
        db.RateLimitRules.RemoveRange(await db.RateLimitRules.ToListAsync());
        await db.SaveChangesAsync();

        await CreateBootstrap(db, ConfiguredScopes()).EnsureInitializedAsync();

        (await db.RateLimitRules.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// A database created before the rules table existed carries a null stamp, so the rules it never
    /// had a chance to seed are backfilled once on upgrade — without disturbing the tiers already
    /// there.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_OnAPreExistingDatabase_BackfillsTheRulesOnce()
    {
        await using var db = CreateDb(nameof(EnsureInitializedAsync_OnAPreExistingDatabase_BackfillsTheRulesOnce));

        db.RateLimitDefaults.Add(new RateLimitDefaultsEntity
        {
            Id = 1,
            Enabled = true,
            Rpm = 4242,
            Burst = 7,
            MaxConcurrentStreams = 9,
            RulesSeededAt = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await CreateBootstrap(db, ConfiguredScopes()).EnsureInitializedAsync();

        (await db.RateLimitRules.CountAsync()).Should().Be(7);
        var defaults = await db.RateLimitDefaults.AsNoTracking().SingleAsync();
        defaults.Rpm.Should().Be(4242, "an existing tier is not overwritten by a backfill");
        defaults.RulesSeededAt.Should().NotBeNull();
    }

    /// <summary>
    /// A malformed target is a typo in a file, not a reason to refuse to start. It is dropped with a
    /// warning; the admin API, where a caller is waiting and can fix it, still rejects the same rule.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_WithAMalformedTarget_SkipsItAndStillBoots()
    {
        await using var db = CreateDb(nameof(EnsureInitializedAsync_WithAMalformedTarget_SkipsItAndStillBoots));

        var options = new RateLimitingOptions();
        // A pair scope needs exactly one separator; this one has none.
        options.TenantModels["acme-local-mock"] = Tier(5);
        options.Models["local-mock"] = Tier(7);

        await CreateBootstrap(db, options).EnsureInitializedAsync();

        var rules = await db.RateLimitRules.AsNoTracking().ToListAsync();
        rules.Should().ContainSingle(r => r.Scope == RateLimitScopeNames.Model);
        rules.Should().NotContain(r => r.Scope == RateLimitScopeNames.TenantModel);
    }

    /// <summary>An unset optional scope is the default shape, so it must not become a row.</summary>
    [Fact]
    public async Task EnsureInitializedAsync_WithNoScopesConfigured_SeedsOnlyTheAuthFailureDefault()
    {
        await using var db = CreateDb(nameof(EnsureInitializedAsync_WithNoScopesConfigured_SeedsOnlyTheAuthFailureDefault));

        await CreateBootstrap(db, new RateLimitingOptions()).EnsureInitializedAsync();

        var rules = await db.RateLimitRules.AsNoTracking().ToListAsync();
        rules.Select(r => r.Scope).Should().BeEquivalentTo([RateLimitScopeNames.AuthFailure]);
    }

    private static RateLimitingOptions ConfiguredScopes()
    {
        var options = new RateLimitingOptions
        {
            Global = Tier(100),
        };

        options.Tenants["acme"] = Tier(50);
        options.ApiKeys["key-1"] = Tier(20);
        options.Models["local-mock"] = new RateLimitTierOptions { Rpm = 7, Burst = 2, MaxConcurrentStreams = 3 };
        options.TenantModels["acme|local-mock"] = Tier(5);
        options.ApiKeyModels["key-1|local-mock"] = Tier(3);

        return options;
    }

    private static RateLimitTierOptions Tier(int rpm) =>
        new() { Rpm = rpm, Burst = 0, MaxConcurrentStreams = 0 };

    private static GatewayDbContext CreateDb(string name)
    {
        var db = PersistenceTestDbContextFactory.CreateInMemory(name);
        db.Database.EnsureCreated();
        return db;
    }

    private static GatewayDbBootstrap CreateBootstrap(GatewayDbContext db, RateLimitingOptions rateLimiting) =>
        new(
            db,
            Options.Create(new GatewayBootstrapOptions { Enabled = false }),
            Options.Create(new GatewayOptions()),
            Options.Create(rateLimiting),
            Options.Create(new QuotaOptions()),
            NullLogger<GatewayDbBootstrap>.Instance);
}
