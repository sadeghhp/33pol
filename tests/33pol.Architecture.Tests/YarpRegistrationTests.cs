namespace Pol33.Architecture.Tests;

public sealed class YarpRegistrationTests
{
    private static readonly string[] ForbiddenTokens =
    [
        "MapReverseProxy",
        "AddReverseProxy(",
    ];

    private static readonly string[] SourceRoots = ["src"];

    [Fact]
    public void Solution_ShouldNotUseReverseProxyRouteTable()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var root in SourceRoots)
        {
            var dir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (var token in ForbiddenTokens)
                {
                    if (text.Contains(token, StringComparison.Ordinal))
                    {
                        offenders.Add($"{file}: contains '{token}'");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(string.Join(Environment.NewLine, offenders));
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

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
