using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Identity;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Security;

namespace Pol33.Persistence.Bootstrap;

public sealed class GatewayDbBootstrap
{
    private readonly GatewayDbContext _db;
    private readonly GatewayBootstrapOptions _options;
    private readonly ILogger<GatewayDbBootstrap> _logger;

    public GatewayDbBootstrap(
        GatewayDbContext db,
        IOptions<GatewayBootstrapOptions> options,
        ILogger<GatewayDbBootstrap> logger)
    {
        _db = db;
        _options = options.Value;
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
}
