namespace Pol33.Architecture.Tests;

public sealed class SolutionLayoutTests
{
    private static readonly string[] ExpectedSrcProjects =
    [
        "33pol.App",
        "33pol.Api",
        "33pol.Billing",
        "33pol.Core",
        "33pol.Observability",
        "33pol.OperatorConsole",
        "33pol.Persistence",
        "33pol.Policy",
        "33pol.Proxy",
        "33pol.Registry",
        "33pol.Security",
    ];

    private static readonly string[] ExpectedTestProjects =
    [
        "33pol.Architecture.Tests",
        "33pol.Billing.Tests",
        "33pol.Conformance.Tests",
        "33pol.Core.Tests",
        "33pol.Integration.Tests",
        "33pol.Observability.Tests",
        "33pol.OperatorConsole.Tests",
        "33pol.Persistence.Tests",
        "33pol.Policy.Tests",
        "33pol.Proxy.Tests",
        "33pol.Registry.Tests",
        "33pol.Security.Tests",
    ];

    [Fact]
    public void Solution_ContainsExpectedProjects()
    {
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        var testsDir = Path.Combine(repoRoot, "tests");

        foreach (var name in ExpectedSrcProjects)
        {
            Assert.True(Directory.Exists(Path.Combine(srcDir, name)), $"Missing src project folder: {name}");
            Assert.True(File.Exists(Path.Combine(srcDir, name, $"{name}.csproj")), $"Missing csproj: {name}");
        }

        foreach (var name in ExpectedTestProjects)
        {
            Assert.True(Directory.Exists(Path.Combine(testsDir, name)), $"Missing test project folder: {name}");
            Assert.True(File.Exists(Path.Combine(testsDir, name, $"{name}.csproj")), $"Missing csproj: {name}");
        }

        Assert.True(File.Exists(Path.Combine(repoRoot, "33pol.sln")), "Missing 33pol.sln at repository root.");
    }

    [Fact]
    public void AllProjects_TargetNet10()
    {
        var repoRoot = FindRepoRoot();
        var buildProps = File.ReadAllText(Path.Combine(repoRoot, "Directory.Build.props"));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", buildProps, StringComparison.Ordinal);
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
