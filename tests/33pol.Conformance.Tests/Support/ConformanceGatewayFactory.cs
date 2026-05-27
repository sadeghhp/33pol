using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;
using Pol33.Core.Http;
using Pol33.Core.Models;
using Pol33.Persistence.DependencyInjection;

namespace Pol33.Conformance.Tests.Support;

internal static class ConformanceGatewayFactory
{
    public static WebApplicationFactory<Program> Create(HttpMessageHandler? upstreamHandler = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
            builder.UseSetting("Gateway:OperatorConsole:Enabled", "false");
            builder.UseSetting("Gateway:RegistryWatchEnabled", "false");
            builder.UseSetting(
                $"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}",
                string.Empty);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBackendHealthStore>();
                services.AddSingleton<IBackendHealthStore, AlwaysHealthyBackendHealthStore>();

                if (upstreamHandler is not null)
                {
                    services.AddHttpClient(UpstreamHttpClientNames.Inference)
                        .ConfigurePrimaryHttpMessageHandler(() => upstreamHandler);
                }
            });
        });
    }

    private sealed class AlwaysHealthyBackendHealthStore : IBackendHealthStore
    {
        public bool IsBackendHealthy(string modelId) => true;

        public BackendHealth? GetHealth(string modelId) => null;

        public IReadOnlyDictionary<string, BackendHealth> GetAllHealth() =>
            new Dictionary<string, BackendHealth>();

        public void SetHealth(BackendHealth health)
        {
        }
    }
}
