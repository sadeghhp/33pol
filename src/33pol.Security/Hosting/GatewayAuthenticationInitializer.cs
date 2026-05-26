using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Persistence;
using Pol33.Persistence.Bootstrap;
using Pol33.Persistence.DependencyInjection;
using Pol33.Security.Hosting;

namespace Pol33.Security.Hosting;

public sealed class GatewayAuthenticationInitializer : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<GatewayAuthenticationInitializer> _logger;

    public GatewayAuthenticationInitializer(
        IServiceProvider services,
        IHostEnvironment environment,
        ILogger<GatewayAuthenticationInitializer> logger)
    {
        _services = services;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName);
        var authState = scope.ServiceProvider.GetRequiredService<GatewayAuthenticationState>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            authState.IsAuthenticationRequired = false;
            _logger.LogInformation("Gateway API key authentication disabled (no database configured)");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var keyCount = await db.ApiKeys.CountAsync(cancellationToken);
        var bootstrapOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<GatewayBootstrapOptions>>()
            .Value;

        if (keyCount == 0 && _environment.IsDevelopment())
        {
            if (bootstrapOptions.Enabled && !string.IsNullOrWhiteSpace(bootstrapOptions.AdminApiKey))
            {
                keyCount = await db.ApiKeys.CountAsync(cancellationToken);
            }

            if (keyCount == 0)
            {
                authState.IsAuthenticationRequired = false;
                _logger.LogWarning(
                    "Gateway API key authentication disabled in Development because no API keys exist in the database");
                return;
            }
        }

        if (keyCount == 0)
        {
            throw new InvalidOperationException(
                "Production gateway requires at least one API key in the database or bootstrap configuration.");
        }

        authState.IsAuthenticationRequired = true;
        _logger.LogInformation("Gateway API key authentication enabled ({KeyCount} keys in database)", keyCount);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
