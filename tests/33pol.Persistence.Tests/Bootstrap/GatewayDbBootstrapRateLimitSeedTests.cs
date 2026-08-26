using Microsoft.Data.Sqlite;
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
/// <para>Every one of these rules was accepted by configuration binding, reported in the "seeded
/// rate-limit settings" log line, documented in the runbook, and then silently dropped: only the
/// default tier and the plan tiers were written.</para>
///
/// <para>Deliberately against real SQLite rather than the EF InMemory provider. InMemory does not
/// enforce unique indexes, so it cannot see the failure that matters here — two configured keys
/// collapsing to one <c>(scope, target)</c> threw a <c>DbUpdateException</c> out of
/// <c>EnsureInitializedAsync</c> on SQLite and the gateway did not start at all, while an InMemory
/// test seeded both rows and passed.</para>
/// </remarks>
public sealed class GatewayDbBootstrapRateLimitSeedTests
{
    [Fact]
    public async Task EnsureInitializedAsync_SeedsEveryConfiguredScopeIntoTheRulesTable()
    {
        await using var scope = await SqliteScope.CreateAsync();

        await CreateBootstrap(scope.Db, ConfiguredScopes()).EnsureInitializedAsync();

        var rules = await scope.Db.RateLimitRules.AsNoTracking().ToListAsync();

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
    /// Two configuration keys differing only in surrounding whitespace are one database row. Seeding
    /// both violated the unique <c>(scope, target)</c> index, and because the seed runs during
    /// startup the exception left the gateway unable to boot at all — from a stray space in a JSON
    /// key. The duplicate is now dropped with a warning and everything else is still seeded.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_WithTargetsThatCollideOnWhitespace_StillBoots()
    {
        await using var scope = await SqliteScope.CreateAsync();

        var options = new RateLimitingOptions();
        options.Models["local-mock"] = Tier(10);
        options.Models["local-mock "] = Tier(20);
        options.Models["other-mock"] = Tier(30);

        var boot = await Record.ExceptionAsync(
            () => CreateBootstrap(scope.Db, options).EnsureInitializedAsync());

        boot.Should().BeNull("a typo in a configuration file must never stop the gateway starting");

        var rules = await scope.Db.RateLimitRules.AsNoTracking()
            .Where(r => r.Scope == RateLimitScopeNames.Model)
            .ToListAsync();

        rules.Select(r => r.TargetKey).Should().BeEquivalentTo(["local-mock", "other-mock"]);
        rules.Single(r => r.TargetKey == "local-mock").Rpm.Should().Be(10);
    }

    /// <summary>
    /// The rule ceiling the admin API enforces has to bind here too. Seeding past it left the
    /// database in a state the API then refused to accept back, so the admin UI — which always sends
    /// the full rule set — could not save any rate-limit change, including switching enforcement off.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_PastTheRuleCeiling_TruncatesInsteadOfSeedingPastIt()
    {
        await using var scope = await SqliteScope.CreateAsync();

        var options = new RateLimitingOptions();
        for (var i = 0; i < RateLimitConfigValidation.MaxRules + 500; i++)
        {
            options.Models[$"m{i:D5}"] = Tier(10);
        }

        await CreateBootstrap(scope.Db, options).EnsureInitializedAsync();

        var rules = await scope.Db.RateLimitRules.AsNoTracking().ToListAsync();
        rules.Should().HaveCount(RateLimitConfigValidation.MaxRules);

        var definitions = rules
            .Select(r => new RateLimitRuleDefinition(r.Scope, r.TargetKey, r.Rpm, r.Burst, r.MaxConcurrentStreams))
            .ToList();
        RateLimitConfigValidation.TryValidateRules(definitions, out var error).Should()
            .BeTrue($"what was seeded must be re-submittable through the admin API, but: {error}");
    }

    /// <summary>
    /// Which rules survive a truncation is fixed by configuration, not by dictionary iteration order,
    /// so two gateways booted from the same file enforce the same thing.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_PastTheRuleCeiling_DropsTheSameRulesEveryTime()
    {
        var options = new RateLimitingOptions();
        for (var i = 0; i < RateLimitConfigValidation.MaxRules + 5; i++)
        {
            options.Models[$"m{i:D5}"] = Tier(10);
        }

        var seeded = new List<string[]>();
        for (var run = 0; run < 2; run++)
        {
            await using var scope = await SqliteScope.CreateAsync();
            await CreateBootstrap(scope.Db, options).EnsureInitializedAsync();
            seeded.Add(await scope.Db.RateLimitRules.AsNoTracking()
                .Where(r => r.Scope == RateLimitScopeNames.Model)
                .Select(r => r.TargetKey)
                .OrderBy(t => t)
                .ToArrayAsync());
        }

        seeded[0].Should().BeEquivalentTo(seeded[1], options => options.WithStrictOrdering());
        seeded[0].Should().NotContain("m02004", "the last rules in target order are the ones dropped");
    }

    /// <summary>
    /// The adaptive switch lives on the defaults row, which the snapshot is loaded from, so leaving
    /// it at its column default made <c>RateLimiting:Adaptive:Enabled</c> another setting with no
    /// effect on a database-backed deployment.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_CarriesTheAdaptiveSwitchOntoTheDefaultsRow()
    {
        await using var scope = await SqliteScope.CreateAsync();

        var options = new RateLimitingOptions();
        options.Adaptive.Enabled = true;

        await CreateBootstrap(scope.Db, options).EnsureInitializedAsync();

        var defaults = await scope.Db.RateLimitDefaults.AsNoTracking().SingleAsync();
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
        await using var scope = await SqliteScope.CreateAsync();

        await CreateBootstrap(scope.Db, ConfiguredScopes()).EnsureInitializedAsync();
        scope.Db.RateLimitRules.RemoveRange(await scope.Db.RateLimitRules.ToListAsync());
        await scope.Db.SaveChangesAsync();

        await CreateBootstrap(scope.Db, ConfiguredScopes()).EnsureInitializedAsync();

        (await scope.Db.RateLimitRules.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// A database created before the rules table existed carries a null stamp, so the rules it never
    /// had a chance to seed are backfilled once on upgrade — without disturbing the tiers already
    /// there.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_OnAPreExistingDatabase_BackfillsTheRulesOnce()
    {
        await using var scope = await SqliteScope.CreateAsync();

        scope.Db.RateLimitDefaults.Add(new RateLimitDefaultsEntity
        {
            Id = 1,
            Enabled = true,
            Rpm = 4242,
            Burst = 7,
            MaxConcurrentStreams = 9,
            RulesSeededAt = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await scope.Db.SaveChangesAsync();

        await CreateBootstrap(scope.Db, ConfiguredScopes()).EnsureInitializedAsync();

        (await scope.Db.RateLimitRules.CountAsync()).Should().Be(7);
        var defaults = await scope.Db.RateLimitDefaults.AsNoTracking().SingleAsync();
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
        await using var scope = await SqliteScope.CreateAsync();

        var options = new RateLimitingOptions();
        // A pair scope needs exactly one separator; this one has none.
        options.TenantModels["acme-local-mock"] = Tier(5);
        options.Models["local-mock"] = Tier(7);

        await CreateBootstrap(scope.Db, options).EnsureInitializedAsync();

        var rules = await scope.Db.RateLimitRules.AsNoTracking().ToListAsync();
        rules.Should().ContainSingle(r => r.Scope == RateLimitScopeNames.Model);
        rules.Should().NotContain(r => r.Scope == RateLimitScopeNames.TenantModel);
    }

    /// <summary>An unset optional scope is the default shape, so it must not become a row.</summary>
    [Fact]
    public async Task EnsureInitializedAsync_WithNoScopesConfigured_SeedsOnlyTheAuthFailureDefault()
    {
        await using var scope = await SqliteScope.CreateAsync();

        await CreateBootstrap(scope.Db, new RateLimitingOptions()).EnsureInitializedAsync();

        var rules = await scope.Db.RateLimitRules.AsNoTracking().ToListAsync();
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

    private static GatewayDbBootstrap CreateBootstrap(GatewayDbContext db, RateLimitingOptions rateLimiting) =>
        new(
            db,
            Options.Create(new GatewayBootstrapOptions { Enabled = false }),
            Options.Create(new GatewayOptions()),
            Options.Create(rateLimiting),
            Options.Create(new QuotaOptions()),
            NullLogger<GatewayDbBootstrap>.Instance);

    /// <summary>
    /// A migrated SQLite database held open for the life of the test, so index and collation
    /// behaviour is the production one.
    /// </summary>
    private sealed class SqliteScope : IAsyncDisposable
    {
        private SqliteConnection _keepAlive = null!;

        public GatewayDbContext Db { get; private set; } = null!;

        public static async Task<SqliteScope> CreateAsync()
        {
            var connectionString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
            var scope = new SqliteScope { _keepAlive = new SqliteConnection(connectionString) };
            await scope._keepAlive.OpenAsync();
            scope.Db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
            await scope.Db.Database.MigrateAsync();
            return scope;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _keepAlive.DisposeAsync();
        }
    }
}
