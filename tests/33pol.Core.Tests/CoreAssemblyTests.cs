using System.Reflection;
using FluentAssertions;

namespace Pol33.Core.Tests;

public sealed class CoreAssemblyTests
{
    [Fact]
    public void Core_HasNoNuGetPackageReferencesBeyondSdk()
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "33pol.Core", "33pol.Core.csproj");

        var content = File.ReadAllText(projectPath);

        content.Should().NotContain("PackageReference", "33pol.Core must not reference NuGet packages beyond the BCL/SDK");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "33pol.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root containing 33pol.sln.");
    }
}
