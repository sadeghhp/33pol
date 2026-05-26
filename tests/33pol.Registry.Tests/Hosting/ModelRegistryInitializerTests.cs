using FluentAssertions;
using Pol33.Registry.Hosting;

namespace Pol33.Registry.Tests.Hosting;

public sealed class ModelRegistryInitializerTests
{
    [Fact]
    public void ResolveConfigPath_FileUnderBaseDirectory_ReturnsCombinedPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var relative = "config/models.json";
        var combined = Path.Combine(baseDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(combined)!);
        File.WriteAllText(combined, "{}");

        try
        {
            var resolved = ModelRegistryInitializer.ResolveConfigPath(relative);
            resolved.Should().Be(combined);
        }
        finally
        {
            if (File.Exists(combined))
            {
                File.Delete(combined);
            }
        }
    }

    [Fact]
    public void ResolveConfigPath_FileMissingUnderBase_FallsBackToRelativePath()
    {
        var relative = $"missing-{Guid.NewGuid():N}.json";

        var resolved = ModelRegistryInitializer.ResolveConfigPath(relative);

        resolved.Should().Be(relative);
    }
}
