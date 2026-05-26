namespace Pol33.Security.Tests;

public sealed class ProjectWiringTests
{
    [Fact]
    public void Assembly_Loads()
    {
        typeof(Pol33.Security.ProjectStub).Assembly.GetName().Name.Should().Be("33pol.Security");
    }
}
