using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

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
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:GatewayDb"] = string.Empty,
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBackendHealthStore>();
                services.AddSingleton<IBackendHealthStore, AlwaysHealthyBackendHealthStore>();

                if (upstreamHandler is not null)
                {
                    services.RemoveAll<HttpMessageInvoker>();
                    services.AddSingleton(upstreamHandler);
                    services.AddSingleton(_ => new HttpMessageInvoker(upstreamHandler, disposeHandler: false));
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
