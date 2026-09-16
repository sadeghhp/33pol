using System.Net.Http.Json;
using System.Text.Json;
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
            // Production (and any non-Development host) rejects the published default pepper at
            // startup. Pin a strong test value so CORS-only factories that flip the environment
            // still boot; configureSettings can override when a test needs a specific pepper.
            builder.UseSetting("Gateway:Bootstrap:KeyPepper", "integration-test-pepper");
            builder.UseSetting("Gateway:Security:KeyPepper", "integration-test-pepper");
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
                RemoveBackendHealthSweep(services);
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
        Action<IDictionary<string, string?>>? configureSettings = null,
        string? environmentName = null)
    {
        var databaseName = Guid.NewGuid().ToString("N");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, environmentName ?? Environments.Development);
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
                RemoveBackendHealthSweep(services);
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

                RemoveBackendHealthSweep(services);
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

    /// <summary>Opts a factory into anonymous <c>/metrics</c>.</summary>
    /// <remarks>
    /// The scrape is Operator-gated, and "authentication is globally disabled" no longer satisfies
    /// an Operator check, so a database-less gateway answers 401 there unless the operator says
    /// otherwise. That is the same contract a scraper-only network uses in production; tests that
    /// assert on exposition content opt in here rather than being served by an oversight.
    /// </remarks>
    public static void AllowAnonymousMetrics(IDictionary<string, string?> settings) =>
        settings["Gateway:Metrics:AllowAnonymous"] = "true";

    /// <summary>The key the database-backed factories above bootstrap unless told otherwise.</summary>
    public const string DefaultAdminApiKey = "sk-33pol-integration-admin-key";

    /// <summary>
    /// A client carrying the bootstrap admin key.
    /// </summary>
    /// <remarks>
    /// The control plane refuses anonymous callers in every configuration, a gateway with
    /// authentication globally disabled included, so an admin surface has to be reached with a real
    /// credential. Tests that used to read <c>/admin/api/...</c> off a database-less gateway were
    /// only ever passing because that gateway was not checking.
    /// </remarks>
    public static HttpClient CreateAdminClient(
        this WebApplicationFactory<Program> factory,
        string adminApiKey = DefaultAdminApiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", adminApiKey);
        return client;
    }

    /// <summary>
    /// Issues an Inference key through the admin API and returns a client carrying it. The
    /// bootstrap key is Admin-only, so anything that both drives traffic and then inspects it needs
    /// two credentials — which is also how a real deployment is used.
    /// </summary>
    /// <param name="grantedModelIds">
    /// Models the key may reach. A key with no grants of its own is allowed nothing, so anything
    /// that actually sends inference has to name what it will ask for.
    /// </param>
    public static async Task<HttpClient> CreateInferenceClientAsync(
        this WebApplicationFactory<Program> factory,
        HttpClient adminClient,
        params string[] grantedModelIds)
    {
        var created = await adminClient.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        created.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var secret = body.RootElement.GetProperty("secret").GetString()!;

        if (grantedModelIds.Length > 0)
        {
            await ModelGrantTestHelpers.GrantApiKeyModelsAsync(
                adminClient,
                Guid.Parse(body.RootElement.GetProperty("id").GetString()!),
                grantedModelIds);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", secret);
        return client;
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

    /// <summary>
    /// Drops the background health sweep. Every factory here substitutes
    /// <see cref="IBackendHealthStore"/> with a stub, so the sweep's verdicts are discarded anyway —
    /// all it does is probe a dead port on a timer and, since probe failures are recorded as error
    /// records, drop unrelated rows into the Errors tab mid-assertion. Tests that want real probing
    /// use the default factory, which is untouched.
    /// </summary>
    private static void RemoveBackendHealthSweep(IServiceCollection services)
    {
        var sweep = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(Pol33.Registry.Health.HealthCheckService));
        if (sweep is not null)
        {
            services.Remove(sweep);
        }
    }

}
