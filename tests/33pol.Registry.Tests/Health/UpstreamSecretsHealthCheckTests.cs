using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Registry.Health;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Health;

public sealed class UpstreamSecretsHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_WithUndecryptableSecret_ReportsDegraded_ThenHealthyOnceReEntered()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            var writer = CreateStore(path, pepper: "original-pepper");
            await writer.PutAsync("model-a", "sk-secret");

            // Pepper rotated: the stored credential no longer decrypts.
            var store = CreateStore(path, pepper: "rotated-pepper");
            var check = new UpstreamSecretsHealthCheck(store);

            var degraded = await check.CheckHealthAsync(new HealthCheckContext());
            degraded.Status.Should().Be(HealthStatus.Degraded);
            degraded.Description.Should().Contain("1 of 1").And.Contain("KeyPepper");

            // The operator re-enters the key under the current pepper; the very next probe must
            // recover without a restart, which a startup-cached verdict could not do.
            await store.PutAsync("model-a", "sk-secret-again");

            var healthy = await check.CheckHealthAsync(new HealthCheckContext());
            healthy.Status.Should().Be(HealthStatus.Healthy);
            healthy.Description.Should().Contain("Verified 1");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task CheckHealth_WithNoStoredSecrets_IsHealthy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        var result = await new UpstreamSecretsHealthCheck(CreateStore(path)).CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    private static FileUpstreamSecretStore CreateStore(string secretsPath, string pepper = "test-pepper")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Security:KeyPepper"] = pepper })
            .Build();

        return new FileUpstreamSecretStore(
            Options.Create(new GatewayOptions
            {
                ModelsConfigPath = Path.Combine(Path.GetTempPath(), "unused-models.json"),
                UpstreamSecretsPath = secretsPath,
            }),
            config,
            new TestHostEnvironment(),
            NullLogger<FileUpstreamSecretStore>.Instance);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
