namespace Pol33.Persistence.Tests;

public sealed class ProjectWiringTests
{
    [Fact]
    public void Assembly_Loads()
    {
        typeof(Pol33.Persistence.ProjectStub).Assembly.GetName().Name.Should().Be("33pol.Persistence");
    }
}
