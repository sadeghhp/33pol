using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Registry.Hosting;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Hosting;

public sealed class UpstreamSecretVerificationHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WithUndecryptableSecret_CompletesWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            var writer = CreateStore(path, pepper: "original-pepper");
            await writer.PutAsync("model-a", "sk-secret");

            var reader = CreateStore(path, pepper: "rotated-pepper");
            var service = new UpstreamSecretVerificationHostedService(
                reader,
                NullLogger<UpstreamSecretVerificationHostedService>.Instance);

            var act = async () => await service.StartAsync(CancellationToken.None);
            await act.Should().NotThrowAsync();

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task StartAsync_WithValidSecrets_CompletesWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            var store = CreateStore(path);
            await store.PutAsync("model-a", "sk-secret");
            var service = new UpstreamSecretVerificationHostedService(
                store,
                NullLogger<UpstreamSecretVerificationHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static FileUpstreamSecretStore CreateStore(string secretsPath, string pepper = "test-pepper")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Security:KeyPepper"] = pepper,
            })
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
