using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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

    /// <summary>
    /// Outside Development a missing/weak pepper disables credential storage rather than crashing
    /// host startup — deployments that never store upstream secrets must still boot.
    /// </summary>
    [Fact]
    public async Task ProductionWithoutPepper_RefusesPutButStillConstructs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            var store = CreateStore(path, pepper: null, environmentName: Environments.Production);

            store.TryGet("model-a", out _).Should().BeFalse();
            var act = async () => await store.PutAsync("model-a", "sk-test");
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*KeyPepper*");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task TryGet_AfterPepperRotation_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            await CreateStore(path, pepper: "original").PutAsync("model-a", "sk-test");
            var rotated = CreateStore(path, pepper: "rotated");

            rotated.TryGet("model-a", out _).Should().BeFalse();
            var (total, undecryptable) = rotated.VerifyStoredSecrets();
            total.Should().Be(1);
            undecryptable.Should().Be(1);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ExistsAsync_AndListExistingAsync_ReportStoredIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            var store = CreateStore(path);
            await store.PutAsync("model-a", "sk-a");
            await store.PutAsync("model-b", "sk-b");

            (await store.ExistsAsync("model-a")).Should().BeTrue();
            (await store.ExistsAsync("missing")).Should().BeFalse();

            var present = await store.ListExistingAsync(["model-a", "missing", "model-b"]);
            present.Should().BeEquivalentTo(["model-a", "model-b"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CorruptSecretsFile_StartsEmptyInsteadOfThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            File.WriteAllText(path, "{not-json");
            var store = CreateStore(path);

            store.TryGet("anything", out _).Should().BeFalse();
            store.VerifyStoredSecrets().Should().Be((0, 0));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static FileUpstreamSecretStore CreateStore(
        string secretsPath,
        string? pepper = "test-pepper",
        string? environmentName = null)
    {
        var values = new Dictionary<string, string?>();
        if (pepper is not null)
        {
            values["Gateway:Security:KeyPepper"] = pepper;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = Options.Create(new GatewayOptions
        {
            ModelsConfigPath = Path.Combine(Path.GetTempPath(), "unused-models.json"),
            UpstreamSecretsPath = secretsPath
        });

        return new FileUpstreamSecretStore(
            options,
            config,
            new TestHostEnvironment { EnvironmentName = environmentName ?? Environments.Production },
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
