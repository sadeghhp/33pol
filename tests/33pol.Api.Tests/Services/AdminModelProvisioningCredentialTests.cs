using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Core.Providers;

namespace Pol33.Api.Tests.Services;

/// <summary>
/// Listing models must resolve stored upstream credentials asynchronously and in bulk. It used to
/// call <c>ExistsAsync(...).GetAwaiter().GetResult()</c> once per model, blocking a thread-pool
/// thread per model on every admin poll — on the same pool the inference path depends on.
/// </summary>
public sealed class AdminModelProvisioningCredentialTests
{
    [Fact]
    public async Task ListModelsAsync_DistinguishesCredentialSources()
    {
        var models = new[]
        {
            new ModelConfig { Id = "no-auth", Url = "http://u" },
            new ModelConfig
            {
                Id = "env-auth",
                Url = "http://u",
                UpstreamAuth = new UpstreamAuthConfig { Type = "bearer", EnvVar = "SOME_KEY" },
            },
            new ModelConfig
            {
                Id = "stored",
                Url = "http://u",
                UpstreamAuth = new UpstreamAuthConfig
                {
                    Type = "bearer",
                    SecretRef = UpstreamSecretRefs.ForModel("stored"),
                },
            },
            new ModelConfig
            {
                Id = "ref-without-secret",
                Url = "http://u",
                UpstreamAuth = new UpstreamAuthConfig
                {
                    Type = "bearer",
                    SecretRef = UpstreamSecretRefs.ForModel("ref-without-secret"),
                },
            },
        };

        var secretStore = new FakeSecretStore(storedModelIds: ["stored"]);
        var service = CreateService(models, secretStore);

        var listed = await service.ListModelsAsync();

        listed.Single(m => m.Model.Id == "no-auth").HasUpstreamCredential.Should().BeFalse();
        listed.Single(m => m.Model.Id == "env-auth").HasUpstreamCredential.Should().BeTrue();
        listed.Single(m => m.Model.Id == "stored").HasUpstreamCredential.Should().BeTrue();
        listed.Single(m => m.Model.Id == "ref-without-secret").HasUpstreamCredential.Should().BeFalse();
    }

    /// <summary>
    /// The regression guard: one bulk call for the whole list, never a per-model fan-out, and never
    /// the blocking single-model overload.
    /// </summary>
    [Fact]
    public async Task ListModelsAsync_UsesASingleBulkSecretLookup()
    {
        var models = Enumerable.Range(0, 25)
            .Select(i => new ModelConfig
            {
                Id = $"m{i}",
                Url = "http://u",
                UpstreamAuth = new UpstreamAuthConfig
                {
                    Type = "bearer",
                    SecretRef = UpstreamSecretRefs.ForModel($"m{i}"),
                },
            })
            .ToArray();

        var secretStore = new FakeSecretStore(storedModelIds: ["m3", "m7"]);
        var service = CreateService(models, secretStore);

        var listed = await service.ListModelsAsync();

        secretStore.ListExistingCalls.Should().Be(1);
        secretStore.ExistsCalls.Should().Be(0);
        listed.Count(m => m.HasUpstreamCredential).Should().Be(2);
    }

    [Fact]
    public async Task ListModelsAsync_WithNoSecretRefModels_SkipsTheSecretStoreEntirely()
    {
        var models = new[] { new ModelConfig { Id = "plain", Url = "http://u" } };
        var secretStore = new FakeSecretStore(storedModelIds: []);
        var service = CreateService(models, secretStore);

        await service.ListModelsAsync();

        secretStore.ListExistingCalls.Should().Be(0);
        secretStore.ExistsCalls.Should().Be(0);
    }

    [Fact]
    public async Task ListModelsAsync_HonoursCancellation()
    {
        var models = new[]
        {
            new ModelConfig
            {
                Id = "stored",
                Url = "http://u",
                UpstreamAuth = new UpstreamAuthConfig
                {
                    Type = "bearer",
                    SecretRef = UpstreamSecretRefs.ForModel("stored"),
                },
            },
        };

        var service = CreateService(models, new FakeSecretStore(storedModelIds: ["stored"]));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await service.ListModelsAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task HasCredentialAsync_ResolvesEachCredentialSource()
    {
        var service = CreateService([], new FakeSecretStore(storedModelIds: ["stored"]));

        (await service.HasCredentialAsync(new ModelConfig { Id = "x", Url = "http://u" }))
            .Should().BeFalse();

        (await service.HasCredentialAsync(new ModelConfig
        {
            Id = "x",
            Url = "http://u",
            UpstreamAuth = new UpstreamAuthConfig { Type = "bearer", EnvVar = "K" },
        })).Should().BeTrue();

        (await service.HasCredentialAsync(new ModelConfig
        {
            Id = "stored",
            Url = "http://u",
            UpstreamAuth = new UpstreamAuthConfig
            {
                Type = "bearer",
                SecretRef = UpstreamSecretRefs.ForModel("stored"),
            },
        })).Should().BeTrue();

        (await service.HasCredentialAsync(new ModelConfig
        {
            Id = "missing",
            Url = "http://u",
            UpstreamAuth = new UpstreamAuthConfig
            {
                Type = "bearer",
                SecretRef = UpstreamSecretRefs.ForModel("missing"),
            },
        })).Should().BeFalse();
    }

    private static AdminModelProvisioningService CreateService(
        IReadOnlyList<ModelConfig> models,
        IUpstreamSecretStore secretStore)
    {
        var commands = Substitute.For<IControlPlaneCommands>();
        commands.ListModels().Returns(models);

        var pricing = Substitute.For<IRateCardAdminService>();
        pricing.GetPricingByModelAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ModelPricing>());

        var services = new ServiceCollection();
        services.AddSingleton(pricing);
        var provider = services.BuildServiceProvider();

        return new AdminModelProvisioningService(
            commands,
            secretStore,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IAuditLogger>());
    }

    /// <summary>
    /// Counts calls so the tests can assert the shape of the access pattern, not just its result.
    /// </summary>
    private sealed class FakeSecretStore(IReadOnlyCollection<string> storedModelIds) : IUpstreamSecretStore
    {
        private readonly HashSet<string> _stored = new(storedModelIds, StringComparer.OrdinalIgnoreCase);

        public int ExistsCalls { get; private set; }

        public int ListExistingCalls { get; private set; }

        public bool TryGet(string modelId, out string? secret)
        {
            secret = _stored.Contains(modelId) ? "secret" : null;
            return secret is not null;
        }

        public Task PutAsync(string modelId, string secret, CancellationToken cancellationToken = default)
        {
            _stored.Add(modelId);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string modelId, CancellationToken cancellationToken = default)
        {
            _stored.Remove(modelId);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string modelId, CancellationToken cancellationToken = default)
        {
            ExistsCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_stored.Contains(modelId));
        }

        public Task<IReadOnlySet<string>> ListExistingAsync(
            IEnumerable<string> modelIds,
            CancellationToken cancellationToken = default)
        {
            ListExistingCalls++;
            cancellationToken.ThrowIfCancellationRequested();

            var present = modelIds
                .Where(_stored.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Task.FromResult<IReadOnlySet<string>>(present);
        }
    }
}
