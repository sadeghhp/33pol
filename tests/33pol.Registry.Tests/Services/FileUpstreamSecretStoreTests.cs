using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class FileUpstreamSecretStoreTests
{
    [Fact]
    public async Task PutAndTryGet_RoundTripsSecret()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            var store = CreateStore(path);
            await store.PutAsync("model-a", "sk-test-secret-key-value");

            store.TryGet("model-a", out var secret).Should().BeTrue();
            secret.Should().Be("sk-test-secret-key-value");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Delete_RemovesSecret()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            var store = CreateStore(path);
            await store.PutAsync("model-a", "sk-test");
            await store.DeleteAsync("model-a");

            store.TryGet("model-a", out _).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static FileUpstreamSecretStore CreateStore(string secretsPath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Security:KeyPepper"] = "test-pepper"
            })
            .Build();

        var options = Options.Create(new GatewayOptions
        {
            ModelsConfigPath = Path.Combine(Path.GetTempPath(), "unused-models.json"),
            UpstreamSecretsPath = secretsPath
        });

        return new FileUpstreamSecretStore(
            options,
            config,
            new TestHostEnvironment(),
            NullLogger<FileUpstreamSecretStore>.Instance);
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Microsoft.Extensions.Hosting.Environments.Production;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
