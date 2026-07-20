using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;
using Pol33.Persistence;
using Pol33.Persistence.DependencyInjection;
using Pol33.Persistence.Bootstrap;
using Pol33.Security.Hosting;

namespace Pol33.Integration.Tests.Support;

internal static class GatewayWebApplicationFactory
{
    public static WebApplicationFactory<Program> Create(
        HttpMessageHandler? upstreamHandler = null,
        IBackendHealthStore? healthStore = null,
        Action<IConfigurationBuilder>? configureConfiguration = null,
        bool clearGatewayDatabase = true,
        Action<IDictionary<string, string?>>? configureSettings = null,
        string? environmentName = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, environmentName ?? Environments.Development);
            builder.UseSetting("Gateway:OperatorConsole:Enabled", "false");
            var extra = new Dictionary<string, string?>();
            configureSettings?.Invoke(extra);
            foreach (var (key, value) in extra)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, config) =>
            {
                configureConfiguration?.Invoke(config);
                if (clearGatewayDatabase)
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:GatewayDb"] = string.Empty,
                    });
                }
            });

            builder.ConfigureServices(services =>
            {
                if (healthStore is not null)
                {
                    services.RemoveAll<IBackendHealthStore>();
                    services.AddSingleton(healthStore);
                }
                else
                {
                    services.RemoveAll<IBackendHealthStore>();
                    services.AddSingleton<IBackendHealthStore, AlwaysHealthyBackendHealthStore>();
                }

                if (upstreamHandler is not null)
                {
                    services.AddHttpClient(Pol33.Core.Http.UpstreamHttpClientNames.Inference)
                        .ConfigurePrimaryHttpMessageHandler(() => upstreamHandler);
                }
            });
        });
    }

    public static WebApplicationFactory<Program> CreateWithInMemoryDatabase(
        string adminApiKey = "sk-33pol-integration-admin-key",
        HttpMessageHandler? upstreamHandler = null,
        IBackendHealthStore? healthStore = null,
        Action<IDictionary<string, string?>>? configureSettings = null)
    {
        var databaseName = Guid.NewGuid().ToString("N");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
            builder.UseSetting($"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}", $"InMemory:{databaseName}");
            builder.UseSetting("Gateway:Bootstrap:Enabled", "true");
            builder.UseSetting("Gateway:Bootstrap:AdminApiKey", adminApiKey);
            builder.UseSetting("Gateway:Bootstrap:KeyPepper", "integration-test-pepper");
            builder.UseSetting("Gateway:Security:KeyPepper", "integration-test-pepper");
            builder.UseSetting("Gateway:OperatorConsole:Enabled", "false");

            var extra = new Dictionary<string, string?>();
            configureSettings?.Invoke(extra);
            foreach (var (key, value) in extra)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                if (healthStore is not null)
                {
                    services.RemoveAll<IBackendHealthStore>();
                    services.AddSingleton(healthStore);
                }
                else
                {
                    services.RemoveAll<IBackendHealthStore>();
                    services.AddSingleton<IBackendHealthStore, AlwaysHealthyBackendHealthStore>();
                }

                if (upstreamHandler is not null)
                {
                    services.AddHttpClient(Pol33.Core.Http.UpstreamHttpClientNames.Inference)
                        .ConfigurePrimaryHttpMessageHandler(() => upstreamHandler);
                }
            });
        });
    }

    /// <summary>
    /// Builds a factory backed by a real SQLite engine rather than the EF InMemory provider, so
    /// EF migrations actually run and constraints, collations and query translation behave as they
    /// do in production. Use this for anything asserting persistence behaviour; the InMemory
    /// variant is faster and fine for everything else.
    ///
    /// The database is shared-cache in-memory, which only survives while a connection to it is
    /// open. That keep-alive connection is registered as a singleton so the DI container closes it
    /// when the factory is disposed, dropping the database with it.
    /// </summary>
    public static WebApplicationFactory<Program> CreateWithSqliteDatabase(
        string adminApiKey = "sk-33pol-integration-admin-key",
        HttpMessageHandler? upstreamHandler = null,
        IBackendHealthStore? healthStore = null,
        Action<IDictionary<string, string?>>? configureSettings = null)
    {
        var connectionString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
            builder.UseSetting($"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}", connectionString);
            builder.UseSetting("Gateway:Bootstrap:Enabled", "true");
            builder.UseSetting("Gateway:Bootstrap:AdminApiKey", adminApiKey);
            builder.UseSetting("Gateway:Bootstrap:KeyPepper", "integration-test-pepper");
            builder.UseSetting("Gateway:Security:KeyPepper", "integration-test-pepper");
            builder.UseSetting("Gateway:OperatorConsole:Enabled", "false");

            var extra = new Dictionary<string, string?>();
            configureSettings?.Invoke(extra);
            foreach (var (key, value) in extra)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(keepAlive);

                services.RemoveAll<IBackendHealthStore>();
                if (healthStore is not null)
                {
                    services.AddSingleton(healthStore);
                }
                else
                {
                    services.AddSingleton<IBackendHealthStore, AlwaysHealthyBackendHealthStore>();
                }

                if (upstreamHandler is not null)
                {
                    services.AddHttpClient(Pol33.Core.Http.UpstreamHttpClientNames.Inference)
                        .ConfigurePrimaryHttpMessageHandler(() => upstreamHandler);
                }
            });
        });
    }

    public static async Task EnsureAuthReadyAsync(WebApplicationFactory<Program> factory)
    {
        _ = factory.Services;

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var keyCount = await db.ApiKeys.CountAsync();
        if (keyCount == 0)
        {
            var bootstrap = scope.ServiceProvider.GetRequiredService<GatewayDbBootstrap>();
            await bootstrap.EnsureInitializedAsync();
            keyCount = await db.ApiKeys.CountAsync();
        }

        factory.Services.GetRequiredService<GatewayAuthenticationState>().IsAuthenticationRequired = keyCount > 0;
    }
}
