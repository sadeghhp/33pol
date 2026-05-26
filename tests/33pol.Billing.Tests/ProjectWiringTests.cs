namespace Pol33.Billing.Tests;

public sealed class ProjectWiringTests
{
    [Fact]
    public void Assembly_Loads()
    {
        typeof(Pol33.Billing.ProjectStub).Assembly.GetName().Name.Should().Be("33pol.Billing");
    }
}
