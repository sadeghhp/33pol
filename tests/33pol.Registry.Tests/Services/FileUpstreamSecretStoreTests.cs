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

    /// <summary>
    /// System.Text.Json rebuilds the secrets dictionary with the ordinal comparer, so after a restart
    /// a credential stored as <c>gpt-4o</c> was invisible to <c>GPT-4o</c>, and case-variant keys
    /// could pile up. The store rewraps the map case-insensitively on load.
    /// </summary>
    [Fact]
    public async Task Reload_KeepsCaseInsensitiveLookupAndCollapsesCaseVariants()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            await CreateStore(path).PutAsync("gpt-4o", "sk-first");

            // Simulate a file written by an older build with two case variants of the same id.
            var text = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var cipher = doc.RootElement.GetProperty("Secrets").GetProperty("gpt-4o").GetString();
            var payload = new { Version = 1, Secrets = new Dictionary<string, string> { ["gpt-4o"] = cipher!, ["GPT-4O"] = cipher! } };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload));

            var reloaded = CreateStore(path);

            reloaded.TryGet("GPT-4o", out var secret).Should().BeTrue();
            secret.Should().Be("sk-first");
            (await reloaded.ExistsAsync("Gpt-4O")).Should().BeTrue();
            reloaded.VerifyStoredSecrets().Should().Be((1, 0), "case variants collapse to a single entry");

            await reloaded.DeleteAsync("GPT-4O");
            reloaded.TryGet("gpt-4o", out _).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentPuts_AllLandOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            var store = CreateStore(path);
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.PutAsync($"model-{i}", $"sk-{i}")));

            var reloaded = CreateStore(path);
            for (var i = 0; i < 20; i++)
            {
                reloaded.TryGet($"model-{i}", out var secret).Should().BeTrue($"model-{i} must survive concurrent persistence");
                secret.Should().Be($"sk-{i}");
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Persist_CreatesTheFileOwnerReadWriteOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"33pol-secrets-{Guid.NewGuid():N}.enc");
        try
        {
            await CreateStore(path).PutAsync("model-a", "sk-test");

            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
