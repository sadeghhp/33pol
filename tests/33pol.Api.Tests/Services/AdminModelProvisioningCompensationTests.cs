using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Pol33.Api.Contracts;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Core.Providers;

namespace Pol33.Api.Tests.Services;

/// <summary>
/// Model metadata and its upstream credential live in two stores with no shared transaction. The
/// previous order (model first, secret second, result ignored) could leave a registered model whose
/// secretRef pointed at a secret that was never written — every request for it then failed with an
/// opaque "upstream auth token not configured", with nothing to indicate why.
/// </summary>
public sealed class AdminModelProvisioningCompensationTests
{
    [Fact]
    public async Task AddAsync_WhenSecretWriteFails_DoesNotRegisterTheModel()
    {
        var commands = Substitute.For<IControlPlaneCommands>();
        var secretStore = new FaultInjectingSecretStore { FailOnPut = true };

        var service = CreateService(commands, secretStore);

        var result = await service.AddAsync(NewRequest("m1", apiKey: "sk-upstream-secret"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("credential");
        await commands.DidNotReceive().AddModelAsync(Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddAsync_WhenModelWriteFails_RollsBackTheSecret()
    {
        var commands = Substitute.For<IControlPlaneCommands>();
        commands.AddModelAsync(Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>())
            .Returns(RegistryMutationResult.Fail("duplicate model id"));

        var secretStore = new FaultInjectingSecretStore();
        var service = CreateService(commands, secretStore);

        var result = await service.AddAsync(NewRequest("m1", apiKey: "sk-upstream-secret"));

        result.Success.Should().BeFalse();
        (await secretStore.ExistsAsync("m1")).Should().BeFalse("the orphaned secret must be rolled back");
    }

    [Fact]
    public async Task AddAsync_WhenRollbackAlsoFails_IsAudited()
    {
        var commands = Substitute.For<IControlPlaneCommands>();
        commands.AddModelAsync(Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>())
            .Returns(RegistryMutationResult.Fail("duplicate model id"));

        var secretStore = new FaultInjectingSecretStore { FailOnDelete = true };
        var audit = Substitute.For<IAuditLogger>();
        var service = CreateService(commands, secretStore, audit);

        var result = await service.AddAsync(NewRequest("m1", apiKey: "sk-upstream-secret"));

        result.Success.Should().BeFalse();
        audit.Received().LogAdminAction("upstream_secret.rollback_failed", Arg.Any<AuditLogEntry>());
    }

    [Fact]
    public async Task AddAsync_HappyPath_StoresSecretAndModel()
    {
        var commands = Substitute.For<IControlPlaneCommands>();
        commands.AddModelAsync(Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>())
            .Returns(RegistryMutationResult.Ok("created"));

        var secretStore = new FaultInjectingSecretStore();
        var service = CreateService(commands, secretStore);

        var result = await service.AddAsync(NewRequest("m1", apiKey: "sk-upstream-secret"));

        result.Success.Should().BeTrue();
        (await secretStore.ExistsAsync("m1")).Should().BeTrue();
        await commands.Received(1).AddModelAsync(Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Update keeps model-first ordering (the model is already serving traffic), but a failed secret
    /// write must be reported rather than swallowed — the operator has to know the new credential
    /// did not take effect.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_WhenSecretWriteFails_SurfacesTheFailure()
    {
        var commands = Substitute.For<IControlPlaneCommands>();
        commands.UpdateModelAsync("m1", Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>())
            .Returns(RegistryMutationResult.Ok("updated"));

        var secretStore = new FaultInjectingSecretStore { FailOnPut = true };
        var service = CreateService(commands, secretStore);

        var result = await service.UpdateAsync("m1", NewRequest("m1", apiKey: "sk-upstream-secret"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("credential");
    }

    [Fact]
    public async Task UpdateAsync_HappyPath_Succeeds()
    {
        var commands = Substitute.For<IControlPlaneCommands>();
        commands.UpdateModelAsync("m1", Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>())
            .Returns(RegistryMutationResult.Ok("updated"));

        var secretStore = new FaultInjectingSecretStore();
        var service = CreateService(commands, secretStore);

        var result = await service.UpdateAsync("m1", NewRequest("m1", apiKey: "sk-upstream-secret"));

        result.Success.Should().BeTrue();
        (await secretStore.ExistsAsync("m1")).Should().BeTrue();
    }

    /// <summary>A request carrying no credential must not be affected by the new ordering.</summary>
    [Fact]
    public async Task AddAsync_WithoutCredential_StillCreatesTheModel()
    {
        var commands = Substitute.For<IControlPlaneCommands>();
        commands.AddModelAsync(Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>())
            .Returns(RegistryMutationResult.Ok("created"));

        var secretStore = new FaultInjectingSecretStore { FailOnPut = true };
        var service = CreateService(commands, secretStore);

        var result = await service.AddAsync(NewRequest("m1", apiKey: null));

        result.Success.Should().BeTrue();
        await commands.Received(1).AddModelAsync(Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Every PrepareModel branch must converge on ModelConfigValidation. The inline-apiKey branch
    /// used to return early, so any rule added to the shared validator silently did not apply to the
    /// most common admin flow. A model id that fails validation must be rejected on that path too.
    /// </summary>
    [Fact]
    public async Task AddAsync_WithApiKey_StillRunsSharedModelValidation()
    {
        var commands = Substitute.For<IControlPlaneCommands>();
        commands.AddModelAsync(Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>())
            .Returns(RegistryMutationResult.Ok("created"));

        var secretStore = new FaultInjectingSecretStore();
        var service = CreateService(commands, secretStore);

        var request = new AdminModelWriteRequest
        {
            Model = new ModelConfig
            {
                Id = "m1",
                Url = "http://upstream:8000",
                // Rejected by ModelConfigValidation; the early return used to skip that check.
                ModelType = "definitely-not-a-model-type",
            },
            ApiKey = "sk-upstream-secret",
        };

        var result = await service.AddAsync(request);

        result.Success.Should().BeFalse();
        await commands.DidNotReceive().AddModelAsync(Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>());
        (await secretStore.ExistsAsync("m1")).Should().BeFalse("validation runs before anything is written");
    }

    /// <summary>The inline-apiKey path must still produce a secretRef-backed credential.</summary>
    [Fact]
    public async Task AddAsync_WithApiKey_StillStoresTheSecretAndSetsSecretRef()
    {
        ModelConfig? persisted = null;
        var commands = Substitute.For<IControlPlaneCommands>();
        commands.AddModelAsync(Arg.Any<ModelConfig>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                persisted = callInfo.ArgAt<ModelConfig>(0);
                return RegistryMutationResult.Ok("created");
            });

        var secretStore = new FaultInjectingSecretStore();
        var service = CreateService(commands, secretStore);

        var result = await service.AddAsync(NewRequest("m1", apiKey: "sk-upstream-secret"));

        result.Success.Should().BeTrue();
        persisted.Should().NotBeNull();
        persisted!.UpstreamAuth.Should().NotBeNull();
        persisted.UpstreamAuth!.SecretRef.Should().Be(UpstreamSecretRefs.ForModel("m1"));
        persisted.UpstreamAuth.EnvVar.Should().BeNullOrEmpty();
        (await secretStore.ExistsAsync("m1")).Should().BeTrue();
    }

    private static AdminModelWriteRequest NewRequest(string id, string? apiKey) =>
        new()
        {
            Model = new ModelConfig { Id = id, Url = "http://upstream:8000" },
            ApiKey = apiKey,
        };

    private static AdminModelProvisioningService CreateService(
        IControlPlaneCommands commands,
        IUpstreamSecretStore secretStore,
        IAuditLogger? audit = null)
    {
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
            audit ?? Substitute.For<IAuditLogger>());
    }

    private sealed class FaultInjectingSecretStore : IUpstreamSecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

        public bool FailOnPut { get; init; }

        public bool FailOnDelete { get; init; }

        public bool TryGet(string modelId, out string? secret) =>
            _secrets.TryGetValue(modelId, out secret);

        public Task PutAsync(string modelId, string secret, CancellationToken cancellationToken = default)
        {
            if (FailOnPut)
            {
                throw new IOException("secret store unavailable");
            }

            _secrets[modelId] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string modelId, CancellationToken cancellationToken = default)
        {
            if (FailOnDelete)
            {
                throw new IOException("secret store unavailable");
            }

            _secrets.Remove(modelId);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.ContainsKey(modelId));

        public Task<IReadOnlySet<string>> ListExistingAsync(
            IEnumerable<string> modelIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(
                modelIds.Where(_secrets.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}
