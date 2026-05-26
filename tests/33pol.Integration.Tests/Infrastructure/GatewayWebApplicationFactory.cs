using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Integration.Tests.Infrastructure;

public class GatewayWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _modelsConfigPath;
    private readonly bool _deleteConfigOnDispose;

    public GatewayWebApplicationFactory(
        bool registryWatchEnabled = false,
        string? modelsConfigPath = null,
        bool deleteConfigOnDispose = true)
    {
        RegistryWatchEnabled = registryWatchEnabled;
        _deleteConfigOnDispose = deleteConfigOnDispose;
        _modelsConfigPath = modelsConfigPath ?? CreateDefaultModelsConfig();
        Upstream = new MockOpenAiUpstreamHandler();
    }

    public MockOpenAiUpstreamHandler Upstream { get; }

    public bool RegistryWatchEnabled { get; }

    public string ModelsConfigPath => _modelsConfigPath;

    public IModelRegistryWriter CreateWriter() =>
        Services.GetRequiredService<IModelRegistryWriter>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:ModelsConfigPath"] = _modelsConfigPath,
                ["Gateway:RegistryWatchEnabled"] = RegistryWatchEnabled ? "true" : "false",
                ["Gateway:ConfigReloadIntervalSeconds"] = "1",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<HttpMessageInvoker>();
            services.AddSingleton(Upstream);
            services.AddSingleton(_ => new HttpMessageInvoker(Upstream, disposeHandler: false));
        });
    }

    public static string CreateDefaultModelsConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-models-{Guid.NewGuid():N}.json");
        var json = """
            {
              "models": [
                {
                  "id": "canonical-model",
                  "url": "http://mock-upstream.local",
                  "maxContextLength": 8192,
                  "aliases": ["alias-model"]
                }
              ]
            }
            """;
        File.WriteAllText(path, json, Encoding.UTF8);
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _deleteConfigOnDispose)
        {
            try
            {
                if (File.Exists(_modelsConfigPath))
                {
                    File.Delete(_modelsConfigPath);
                }
            }
            catch (IOException)
            {
            }
        }

        base.Dispose(disposing);
    }
}
