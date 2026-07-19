using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.Identity;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Security;

namespace Pol33.Persistence.Bootstrap;

public sealed class GatewayDbBootstrap
{
    private readonly GatewayDbContext _db;
    private readonly GatewayBootstrapOptions _options;
    private readonly GatewayOptions _gatewayOptions;
    private readonly RateLimitingOptions _rateLimitingOptions;
    private readonly ILogger<GatewayDbBootstrap> _logger;

    public GatewayDbBootstrap(
        GatewayDbContext db,
        IOptions<GatewayBootstrapOptions> options,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<RateLimitingOptions> rateLimitingOptions,
        ILogger<GatewayDbBootstrap> logger)
    {
        _db = db;
        _options = options.Value;
        _gatewayOptions = gatewayOptions.Value;
        _rateLimitingOptions = rateLimitingOptions.Value;
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

    private async Task SeedRateLimitSettingsAsync(CancellationToken cancellationToken)
    {
        if (await _db.RateLimitDefaults.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var defaults = _rateLimitingOptions.Default;
        _db.RateLimitDefaults.Add(new RateLimitDefaultsEntity
        {
            Id = 1,
            Rpm = defaults.Rpm,
            Burst = defaults.Burst,
            MaxConcurrentStreams = defaults.MaxConcurrentStreams,
            UpdatedAt = now,
        });

        foreach (var (slug, tier) in _rateLimitingOptions.Plans)
        {
            _db.RateLimitPlans.Add(new RateLimitPlanEntity
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                Rpm = tier.Rpm,
                Burst = tier.Burst,
                MaxConcurrentStreams = tier.MaxConcurrentStreams,
                UpdatedAt = now,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Seeded rate-limit settings (default + {PlanCount} plan tier(s)) from configuration.",
            _rateLimitingOptions.Plans.Count);
    }
}
