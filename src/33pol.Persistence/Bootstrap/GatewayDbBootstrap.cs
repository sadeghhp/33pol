using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.RateLimiting;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Mapping;
using Pol33.Persistence.Security;

namespace Pol33.Persistence.Bootstrap;

public sealed class GatewayDbBootstrap
{
    private readonly GatewayDbContext _db;
    private readonly GatewayBootstrapOptions _options;
    private readonly GatewayOptions _gatewayOptions;
    private readonly RateLimitingOptions _rateLimitingOptions;
    private readonly QuotaOptions _quotaOptions;
    private readonly ILogger<GatewayDbBootstrap> _logger;

    public GatewayDbBootstrap(
        GatewayDbContext db,
        IOptions<GatewayBootstrapOptions> options,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<RateLimitingOptions> rateLimitingOptions,
        IOptions<QuotaOptions> quotaOptions,
        ILogger<GatewayDbBootstrap> logger)
    {
        _db = db;
        _options = options.Value;
        _gatewayOptions = gatewayOptions.Value;
        _rateLimitingOptions = rateLimitingOptions.Value;
        _quotaOptions = quotaOptions.Value;
        _logger = logger;
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_db.Database.IsRelational())
        {
            await _db.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            await _db.Database.EnsureCreatedAsync(cancellationToken);
        }

        // Seed database-backed config from appsettings once (idempotent), independent of whether the
        // default tenant/admin-key bootstrap is enabled.
        await SeedCorsSettingsAsync(cancellationToken);
        await SeedRateLimitSettingsAsync(cancellationToken);
        await SeedModelRoutesAsync(cancellationToken);
        await SeedQuotaSettingsAsync(cancellationToken);

        if (!_options.Enabled)
        {
            _logger.LogInformation("Gateway database bootstrap is disabled");
            return;
        }

        if (await _db.Tenants.AnyAsync(cancellationToken))
        {
            return;
        }

        var adminKey = _options.AdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            _logger.LogWarning(
                "Gateway database is empty and {Setting} is not set; skipping bootstrap tenant creation",
                $"{GatewayBootstrapOptions.SectionName}:AdminApiKey");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var prefix = ApiKeyHashing.CreatePrefix(adminKey);
        var hash = ApiKeyHashing.Hash(adminKey, _options.KeyPepper);

        _db.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = _options.TenantSlug,
            Name = _options.TenantName,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.ApiKeys.Add(new ApiKeyEntity
        {
            Id = keyId,
            TenantId = tenantId,
            KeyHash = hash,
            KeyPrefix = prefix,
            Role = ApiKeyRole.Admin,
            Scopes = ["admin"],
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Bootstrapped tenant {TenantSlug} with admin API key prefix {KeyPrefix}",
            _options.TenantSlug,
            prefix);
    }

    private async Task SeedCorsSettingsAsync(CancellationToken cancellationToken)
    {
        if (await _db.CorsSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        var origins = _gatewayOptions.Cors.GetNormalizedOrigins();
        _db.CorsSettings.Add(new CorsSettingsEntity
        {
            Id = 1,
            AllowedOrigins = [.. origins],
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Seeded CORS settings with {OriginCount} allowed origin(s) from configuration.", origins.Length);
    }

    /// <summary>
    /// Copies the configured rate-limit settings into the database on first boot.
    /// </summary>
    /// <remarks>
    /// The database is the source of truth once the gateway is running — the config snapshot is
    /// loaded from it, not from appsettings — so anything not seeded here is configuration the
    /// operator wrote and the gateway silently ignores. That is why the scoped rules are seeded
    /// alongside the default and plan tiers rather than only through the admin API: without it every
    /// <c>Models</c>, <c>ApiKeys</c>, <c>TenantModels</c>, <c>ApiKeyModels</c>, <c>Tenants</c>,
    /// <c>Global</c> and <c>AuthFailure</c> entry in appsettings was accepted, logged as seeded, and
    /// then never enforced on any database-backed deployment.
    /// </remarks>
    private async Task SeedRateLimitSettingsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var defaults = await _db.RateLimitDefaults
            .FirstOrDefaultAsync(d => d.Id == 1, cancellationToken);

        var seededTiers = false;
        if (defaults is null)
        {
            var tier = _rateLimitingOptions.Default;
            defaults = new RateLimitDefaultsEntity
            {
                Id = 1,
                Enabled = _rateLimitingOptions.Enabled,
                Rpm = tier.Rpm,
                Burst = tier.Burst,
                MaxConcurrentStreams = tier.MaxConcurrentStreams,
                // Carried across for the same reason as everything else here: the governor reads the
                // live snapshot, which is loaded from this row, so leaving it false made
                // RateLimiting:Adaptive:Enabled a setting with no effect.
                AdaptiveEnabled = _rateLimitingOptions.Adaptive.Enabled,
                UpdatedAt = now,
            };
            _db.RateLimitDefaults.Add(defaults);

            foreach (var (slug, planTier) in _rateLimitingOptions.Plans)
            {
                _db.RateLimitPlans.Add(new RateLimitPlanEntity
                {
                    Id = Guid.NewGuid(),
                    Slug = slug,
                    Rpm = planTier.Rpm,
                    Burst = planTier.Burst,
                    MaxConcurrentStreams = planTier.MaxConcurrentStreams,
                    UpdatedAt = now,
                });
            }

            seededTiers = true;
        }

        // Stamped once, never re-run. Seeding on an empty rules table instead would silently restore
        // every appsettings rule the first time the gateway restarted after an operator deleted them
        // through the admin API. A database created before this table existed has a null stamp, so it
        // is backfilled from configuration exactly once on upgrade.
        var seededRules = 0;
        if (defaults.RulesSeededAt is null)
        {
            foreach (var rule in BuildConfiguredRules())
            {
                _db.RateLimitRules.Add(new RateLimitRuleEntity
                {
                    Id = Guid.NewGuid(),
                    Scope = rule.Scope,
                    TargetKey = rule.TargetKey,
                    Rpm = rule.Rpm,
                    Burst = rule.Burst,
                    MaxConcurrentStreams = rule.MaxConcurrentStreams,
                    UpdatedAt = now,
                });
                seededRules++;
            }

            defaults.RulesSeededAt = now;
        }
        else if (!seededTiers)
        {
            return;
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (seededTiers)
        {
            _logger.LogInformation(
                "Seeded rate-limit settings (default + {PlanCount} plan tier(s) + {RuleCount} scoped rule(s)) from configuration.",
                _rateLimitingOptions.Plans.Count,
                seededRules);
        }
        else
        {
            _logger.LogInformation(
                "Backfilled {RuleCount} scoped rate-limit rule(s) from configuration.",
                seededRules);
        }
    }

    /// <summary>
    /// Flattens the configured scope maps into the rule set to seed, dropping anything malformed,
    /// anything duplicated, and anything past the ceiling.
    /// </summary>
    /// <remarks>
    /// <para>Every rejection here is a warning and a skip, never a throw. This runs during startup,
    /// so refusing to boot because one <c>TenantModels</c> key is missing its separator would turn a
    /// typo into an outage. The admin API validates the same rules and does reject them outright,
    /// because there a caller is waiting for an answer and can fix it.</para>
    ///
    /// <para>The set is validated as a whole, not rule by rule. Validating each in isolation gave
    /// every call a fresh duplicate set and a fresh count, so neither the uniqueness rule nor the
    /// ceiling could ever fire — and two configured keys differing only in surrounding whitespace
    /// produced two rows with the same (scope, target), which the unique index rejects. On SQLite
    /// that threw out of <c>EnsureInitializedAsync</c> and the gateway did not start; on a provider
    /// without index enforcement it seeded both and let load order decide which one applied.</para>
    ///
    /// <para>Ordering is fixed — scope order, then target within each scope — so the seeded rows, and
    /// which rules a ceiling truncation drops, are the same on every boot from the same file rather
    /// than a function of dictionary iteration order.</para>
    /// </remarks>
    private List<RateLimitRuleDefinition> BuildConfiguredRules()
    {
        var rules = new List<RateLimitRuleDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddSingleton(RateLimitScopeNames.Global, _rateLimitingOptions.Global);
        AddMap(RateLimitScopeNames.Tenant, _rateLimitingOptions.Tenants);
        AddMap(RateLimitScopeNames.ApiKey, _rateLimitingOptions.ApiKeys);
        AddMap(RateLimitScopeNames.Model, _rateLimitingOptions.Models);
        AddMap(RateLimitScopeNames.TenantModel, _rateLimitingOptions.TenantModels);
        AddMap(RateLimitScopeNames.ApiKeyModel, _rateLimitingOptions.ApiKeyModels);
        AddSingleton(RateLimitScopeNames.AuthFailure, _rateLimitingOptions.AuthFailure);

        if (rules.Count > RateLimitConfigValidation.MaxRules)
        {
            // Truncated rather than seeded past the ceiling, and said out loud. Seeding all of them
            // put the database in a state the admin API then refused to accept back, so an operator
            // could not save any rate-limit change — including switching enforcement off — until
            // rows were deleted by hand.
            _logger.LogWarning(
                "Configured rate-limit rules exceed the {MaxRules} rule ceiling ({Configured} configured); " +
                "the last {Dropped} in scope order were not seeded. Use plan tiers rather than per-target rules at this scale.",
                RateLimitConfigValidation.MaxRules,
                rules.Count,
                rules.Count - RateLimitConfigValidation.MaxRules);
            rules.RemoveRange(
                RateLimitConfigValidation.MaxRules,
                rules.Count - RateLimitConfigValidation.MaxRules);
        }

        // Belt and braces: whatever survived the per-rule checks must also satisfy the validator the
        // admin API applies, or the two paths disagree about what a valid rule set is. A set that
        // fails here is a bug in this method, not in the operator's file — so it is logged loudly and
        // nothing is seeded, which leaves a startable gateway enforcing its default tier.
        if (!RateLimitConfigValidation.TryValidateRules(rules, out var setError))
        {
            _logger.LogError(
                "Configured rate-limit rules did not validate as a set ({Error}); none were seeded.",
                setError);
            return [];
        }

        return rules;

        void AddSingleton(string scope, RateLimitTierOptions tier) =>
            Add(scope, RateLimitScopeNames.SingletonTarget, tier);

        void AddMap(string scope, Dictionary<string, RateLimitTierOptions> tiers)
        {
            foreach (var target in tiers.Keys.OrderBy(static k => k, StringComparer.Ordinal))
            {
                Add(scope, target, tiers[target]);
            }
        }

        void Add(string scope, string target, RateLimitTierOptions? tier)
        {
            // An unset optional scope is the default shape, not a configuration error.
            if (tier is null || tier.EnforcesNothing)
            {
                return;
            }

            // Deliberately not trimmed. Trimming here hid the whitespace the validator rejects and
            // then produced the duplicate that whitespace would otherwise only have annoyed someone
            // with: "gpt-4" and "gpt-4 " are distinct configuration keys but one database row.
            var definition = new RateLimitRuleDefinition(
                scope,
                target,
                tier.Rpm,
                tier.Burst,
                tier.MaxConcurrentStreams);

            if (!RateLimitConfigValidation.TryValidateRules([definition], out var error))
            {
                _logger.LogWarning(
                    "Ignoring configured rate-limit rule '{Scope}:{Target}': {Error}",
                    scope,
                    target,
                    error);
                return;
            }

            if (!seen.Add(definition.Identity))
            {
                _logger.LogWarning(
                    "Ignoring configured rate-limit rule '{Scope}:{Target}': another rule already targets it.",
                    scope,
                    target);
                return;
            }

            rules.Add(definition);
        }
    }

    private async Task SeedQuotaSettingsAsync(CancellationToken cancellationToken)
    {
        if (await _db.QuotaSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        _db.QuotaSettings.Add(new QuotaSettingsEntity
        {
            Id = 1,
            DefaultMonthlyTokenLimit = _quotaOptions.DefaultMonthlyTokenLimit,
            SoftLimitRatio = (decimal)_quotaOptions.SoftLimitRatio,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Seeded quota settings (limit {Limit}, soft ratio {Ratio}) from configuration.",
            _quotaOptions.DefaultMonthlyTokenLimit,
            _quotaOptions.SoftLimitRatio);
    }

    private static readonly JsonSerializerOptions ModelsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private async Task SeedModelRoutesAsync(CancellationToken cancellationToken)
    {
        if (await _db.ModelRoutes.AnyAsync(cancellationToken))
        {
            return;
        }

        var path = ResolveModelsConfigPath(_gatewayOptions.ModelsConfigPath);
        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "Models configuration file not found at {ConfigPath}; model_routes left empty until an admin write.",
                path);
            return;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var config = JsonSerializer.Deserialize<ModelRegistryConfig>(json, ModelsJsonOptions);
        if (config?.Models is null || config.Models.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var model in config.Models)
        {
            _db.ModelRoutes.Add(ModelRouteEntityMapper.ToEntity(model, now));
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Seeded {ModelCount} model route(s) from {ConfigPath}.", config.Models.Count, path);
    }

    private static string ResolveModelsConfigPath(string modelsConfigPath)
    {
        var combined = Path.Combine(AppContext.BaseDirectory, modelsConfigPath);
        return File.Exists(combined) ? combined : modelsConfigPath;
    }
}
