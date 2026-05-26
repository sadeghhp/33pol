namespace Pol33.Policy.Tests;

public sealed class ProjectWiringTests
{
    [Fact]
    public void Assembly_Loads()
    {
        typeof(Pol33.Policy.ProjectStub).Assembly.GetName().Name.Should().Be("33pol.Policy");
    }
}
