using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
        Action<IDictionary<string, string?>>? configureSettings = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
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
