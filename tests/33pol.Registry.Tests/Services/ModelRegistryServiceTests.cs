using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Models;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class ModelRegistryServiceTests
{
    private readonly ModelRegistryService _sut = new(NullLogger<ModelRegistryService>.Instance);

    [Fact]
    public async Task LoadModelsAsync_ValidFile_ReturnsCorrectCount()
    {
        var path = TestDataPath("valid-models.json");

        await _sut.LoadModelsAsync(path);

        _sut.GetAllModels().Should().HaveCount(2);
        _sut.ModelExists("canonical-a").Should().BeTrue();
        _sut.ModelExists("canonical-b").Should().BeTrue();
    }

    [Fact]
    public async Task TryGetModel_Alias_ResolvesToCanonicalConfig()
    {
        await _sut.LoadModelsAsync(TestDataPath("valid-models.json"));

        var found = _sut.TryGetModel("alias-a", out var model);

        found.Should().BeTrue();
        model!.Id.Should().Be("canonical-a");
        model.Url.Should().Be("http://backend-a:8000");
    }

    [Fact]
    public async Task TryGetModel_DifferentCasing_ResolvesModel()
    {
        await _sut.LoadModelsAsync(TestDataPath("valid-models.json"));

        _sut.TryGetModel("ALIAS-A", out var byAlias).Should().BeTrue();
        _sut.TryGetModel("Canonical-A", out var byId).Should().BeTrue();

        byAlias!.Id.Should().Be(byId!.Id);
    }

    /// <summary>
    /// ModelConfig is mutable. A caller that edits what TryGetModel hands back (normalising a URL,
    /// touching aliases) must not alter the live routing table for every other request.
    /// </summary>
    [Fact]
    public async Task TryGetModel_ReturnsACopy_SoCallersCannotMutateTheRoutingTable()
    {
        await _sut.LoadModelsAsync(TestDataPath("valid-models.json"));

        _sut.TryGetModel("canonical-a", out var first).Should().BeTrue();
        first!.Url = "http://tampered:1";
        first.Aliases.Clear();

        _sut.TryGetModel("canonical-a", out var second).Should().BeTrue();
        second.Should().NotBeSameAs(first);
        second!.Url.Should().Be("http://backend-a:8000");
        _sut.TryGetModel("alias-a", out _).Should().BeTrue();
    }

    [Fact]
    public async Task LoadModelsAsync_InvalidJson_ThrowsJsonException()
    {
        var act = () => _sut.LoadModelsAsync(TestDataPath("invalid-models.json"));

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task LoadModelsAsync_EmptyModelsArray_KeepsPreviousRegistry()
    {
        await _sut.LoadModelsAsync(TestDataPath("valid-models.json"));

        var emptyPath = Path.Combine(Path.GetTempPath(), $"33pol-empty-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(emptyPath, await File.ReadAllTextAsync(TestDataPath("empty-models.json")));

        try
        {
            await _sut.LoadModelsAsync(emptyPath);

            _sut.GetAllModels().Should().HaveCount(2);
            _sut.TryGetModel("alias-a", out _).Should().BeTrue();
        }
        finally
        {
            File.Delete(emptyPath);
        }
    }

    [Fact]
    public async Task LoadModelsAsync_ConcurrentReadDuringReload_DoesNotThrow()
    {
        await _sut.LoadModelsAsync(TestDataPath("valid-models.json"));

        var reloadPath = Path.Combine(Path.GetTempPath(), $"33pol-reload-{Guid.NewGuid():N}.json");
        var reloadJson = """
            {
              "models": [
                {
                  "id": "reload-only",
                  "url": "http://reload:9000",
                  "aliases": ["reload-alias"]
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(reloadPath, reloadJson);

        try
        {
            var readGate = new ManualResetEventSlim(false);
            var reloadStarted = new ManualResetEventSlim(false);
            Exception? reloadError = null;

            var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                readGate.Wait();
                for (var i = 0; i < 200; i++)
                {
                    _sut.TryGetModel(i % 2 == 0 ? "alias-a" : "canonical-b", out ModelConfig? _);
                    _ = _sut.GetAllModels().Count;
                }
            })).ToArray();

            var reloadTask = Task.Run(async () =>
            {
                try
                {
                    reloadStarted.Set();
                    await _sut.LoadModelsAsync(reloadPath);
                }
                catch (Exception ex)
                {
                    reloadError = ex;
                }
            });

            readGate.Set();
            await Task.WhenAll(readers);
            await reloadTask;

            reloadError.Should().BeNull();
            _sut.TryGetModel("reload-alias", out var model).Should().BeTrue();
            model!.Id.Should().Be("reload-only");
        }
        finally
        {
            File.Delete(reloadPath);
        }
    }

    [Fact]
    public void GetBackendUrl_UnknownModel_ReturnsNull()
    {
        _sut.GetBackendUrl("missing").Should().BeNull();
    }

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
}
