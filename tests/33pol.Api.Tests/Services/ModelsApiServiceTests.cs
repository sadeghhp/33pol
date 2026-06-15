using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Api.Tests.Services;

public sealed class ModelsApiServiceTests
{
    [Fact]
    public void ListHealthyModels_ExcludesUnhealthyBackends()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "healthy-a", Url = "http://a" },
            new ModelConfig { Id = "sick-b", Url = "http://b" },
        ]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("healthy-a").Returns(true);
        health.IsBackendHealthy("sick-b").Returns(false);

        var grants = Substitute.For<IModelGrantService>();
        grants.IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = new ModelsApiService(registry, health, grants);

        var list = service.ListHealthyModels();

        list.Data.Should().ContainSingle(m => m.Id == "healthy-a");
        list.Object.Should().Be("list");
    }

    [Fact]
    public void TryGetModel_WithAlias_ReturnsCanonicalId()
    {
        var model = new ModelConfig
        {
            Id = "canonical/id",
            Url = "http://backend",
            MaxContextLength = 4096,
            Aliases = ["alias-name"],
        };

        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("alias-name", out Arg.Any<ModelConfig?>())
            .Returns(callInfo =>
            {
                callInfo[1] = model;
                return true;
            });

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("canonical/id").Returns(false);

        var grants = Substitute.For<IModelGrantService>();
        grants.IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = new ModelsApiService(registry, health, grants);

        var (response, error) = service.TryGetModel("alias-name");

        error.Should().BeNull();
        response!.Id.Should().Be("canonical/id");
        response.Available.Should().BeFalse();
        response.MaxModelLen.Should().Be(4096);
    }

    [Fact]
    public void ListPublicHealthyModels_ReturnsOnlyPublicHealthy()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "public-a", Url = "http://a", PublicAccess = true },
            new ModelConfig { Id = "private-b", Url = "http://b" },
        ]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy(Arg.Any<string>()).Returns(true);

        var service = new ModelsApiService(registry, health, Substitute.For<IModelGrantService>());

        var list = service.ListPublicHealthyModels();

        list.Data.Should().ContainSingle(m => m.Id == "public-a");
    }

    [Fact]
    public async Task ListHealthyModelsAsync_IncludesPublicWithoutGrantCheck()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "public-a", Url = "http://a", PublicAccess = true },
            new ModelConfig { Id = "private-b", Url = "http://b" },
        ]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy(Arg.Any<string>()).Returns(true);

        var grants = Substitute.For<IModelGrantService>();
        grants.IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), "private-b", Arg.Any<CancellationToken>())
            .Returns(true);

        var service = new ModelsApiService(registry, health, grants);
        var list = await service.ListHealthyModelsAsync(Guid.NewGuid(), Guid.NewGuid());

        list.Data.Select(m => m.Id).Should().BeEquivalentTo(["public-a", "private-b"]);
        await grants.DidNotReceive()
            .IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), "public-a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TryGetPublicModel_NonPublic_ReturnsNotFound()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("private", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig { Id = "private", Url = "http://b" };
                return true;
            });

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("private").Returns(true);
        var service = new ModelsApiService(registry, health, Substitute.For<IModelGrantService>());

        var (_, error) = service.TryGetPublicModel("private");

        error!.Error.Code.Should().Be("model_not_found");
    }

    [Fact]
    public void TryGetModel_UnknownModel_ReturnsNotFoundError()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("missing", out Arg.Any<ModelConfig?>()).Returns(false);

        var health = Substitute.For<IBackendHealthStore>();
        var grants = Substitute.For<IModelGrantService>();
        grants.IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = new ModelsApiService(registry, health, grants);

        var (response, error) = service.TryGetModel("missing");

        response.Should().BeNull();
        error!.Error.Code.Should().Be("model_not_found");
        error.Error.Type.Should().Be("invalid_request_error");
    }

    [Fact]
    public async Task ListHealthyModelsAsync_GrantDenied_ExcludesPrivateModel()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "public-a", Url = "http://a", PublicAccess = true },
            new ModelConfig { Id = "private-b", Url = "http://b" },
            new ModelConfig { Id = "private-c", Url = "http://c" },
        ]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy(Arg.Any<string>()).Returns(true);

        var grants = Substitute.For<IModelGrantService>();
        grants.IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), "private-b", Arg.Any<CancellationToken>())
            .Returns(true);
        grants.IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), "private-c", Arg.Any<CancellationToken>())
            .Returns(false);

        var service = new ModelsApiService(registry, health, grants);
        var list = await service.ListHealthyModelsAsync(Guid.NewGuid(), Guid.NewGuid());

        list.Data.Select(m => m.Id).Should().BeEquivalentTo(["public-a", "private-b"]);
    }

    [Fact]
    public async Task TryGetModelAsync_GrantDenied_ReturnsNotFound()
    {
        var model = new ModelConfig { Id = "private-b", Url = "http://b" };
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("private-b", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = model;
                return true;
            });

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("private-b").Returns(true);

        var grants = Substitute.For<IModelGrantService>();
        grants.IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), "private-b", Arg.Any<CancellationToken>())
            .Returns(false);

        var service = new ModelsApiService(registry, health, grants);
        var (response, error) = await service.TryGetModelAsync(
            "private-b",
            Guid.NewGuid(),
            Guid.NewGuid());

        response.Should().BeNull();
        error!.Error.Code.Should().Be("model_not_found");
    }
}
