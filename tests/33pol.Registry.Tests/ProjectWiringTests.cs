using Pol33.Registry.Services;

namespace Pol33.Registry.Tests;

public sealed class ProjectWiringTests
{
    [Fact]
    public void Assembly_Loads()
    {
        typeof(ModelRegistryService).Assembly.GetName().Name.Should().Be("33pol.Registry");
    }
}