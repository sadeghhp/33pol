using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

/// <summary>
/// What a reload reports when the file it reads cannot be parsed, and what the registry keeps.
/// </summary>
/// <remarks>
/// The file is the source of truth only for a gateway with no database — no <c>IModelRouteRepository</c>
/// is registered, so <see cref="ModelRegistryLoader"/> falls back to models.json. That mode still
/// serves inference, so a corrupt file has to fail loudly and leave the running routes alone.
/// <para>
/// Covered here rather than through <c>POST /admin/api/config/reload</c> because that endpoint is no
/// longer reachable in the mode this behavior belongs to: the control plane refuses anonymous
/// callers in every configuration, and a database-less gateway has no key store to authenticate
/// against. The HTTP test that used to assert this was passing only because the control plane was
/// not checking.
/// </para>
/// </remarks>
public sealed class ModelRegistryConfigReloadTests : IDisposable
{
    private readonly string _configPath =
        Path.Combine(Path.GetTempPath(), $"models-{Guid.NewGuid():N}.json");

    private const string OneGoodModel =
        """{ "models": [ { "id": "keep-me", "url": "http://localhost:8080", "aliases": [] } ] }""";

    private ModelRegistryConfigReload CreateDatabaseLessReload(out ModelRegistryService registry)
    {
        // No IModelRouteRepository registered: this is the DB-less path.
        var provider = new ServiceCollection().BuildServiceProvider();
        registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var loader = new ModelRegistryLoader(
            provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            Options.Create(new GatewayOptions { ModelsConfigPath = _configPath }),
            NullLogger<ModelRegistryLoader>.Instance);

        return new ModelRegistryConfigReload(
            loader, registry, NullLogger<ModelRegistryConfigReload>.Instance);
    }

    [Fact]
    public async Task ReloadAsync_WhenTheFileBecameUnparseable_ReportsErrorAndKeepsTheLoadedModels()
    {
        await File.WriteAllTextAsync(_configPath, OneGoodModel);
        var reload = CreateDatabaseLessReload(out var registry);

        (await reload.ReloadAsync()).Status.Should().Be("success");
        registry.GetAllModels().Should().ContainSingle(m => m.Id == "keep-me");

        await File.WriteAllTextAsync(_configPath, "{ not-json");
        var result = await reload.ReloadAsync();

        result.Status.Should().Be("error");
        result.SuggestedStatusCode.Should().Be(500, "a reload that did not happen is not a success");
        registry.GetAllModels().Should().ContainSingle(
            m => m.Id == "keep-me",
            "a failed reload must leave the routes that are actually serving traffic in place");
    }

    [Fact]
    public async Task ReloadAsync_LeavesNoReloadInProgressFlagBehindAfterAFailure()
    {
        await File.WriteAllTextAsync(_configPath, "{ not-json");
        var reload = CreateDatabaseLessReload(out _);

        (await reload.ReloadAsync()).Status.Should().Be("error");

        reload.IsReloadInProgress.Should().BeFalse(
            "readiness reads this flag; a stuck one reports the gateway as reloading forever");
    }

    public void Dispose()
    {
        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }
    }
}
