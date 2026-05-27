using FluentAssertions;
using Pol33.Core.Models;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class ModelRegistryPersistenceTests
{
    [Fact]
    public async Task WriteAtomicAsync_WritesValidJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"33pol-persist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "models.json");

        try
        {
            await ModelRegistryPersistence.WriteAtomicAsync(
                path,
                [new ModelConfig { Id = "a", Url = "http://a", Aliases = [] }],
                CancellationToken.None);

            var json = await File.ReadAllTextAsync(path);
            json.Should().Contain("\"id\": \"a\"");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteAtomicAsync_OverwritesExistingFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"33pol-persist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "models.json");
        await File.WriteAllTextAsync(path, """{ "models": [] }""");

        try
        {
            await ModelRegistryPersistence.WriteAtomicAsync(
                path,
                [new ModelConfig { Id = "b", Url = "http://b", Aliases = ["x"] }],
                CancellationToken.None);

            var config = ModelRegistryPersistence.Deserialize(await File.ReadAllTextAsync(path));
            config.Models.Should().ContainSingle(m => m.Id == "b");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
