namespace Pol33.Observability.Tests;

public sealed class ProjectWiringTests
{
    [Fact]
    public void Assembly_Loads()
    {
        typeof(Pol33.Observability.ProjectStub).Assembly.GetName().Name.Should().Be("33pol.Observability");
    }
}
