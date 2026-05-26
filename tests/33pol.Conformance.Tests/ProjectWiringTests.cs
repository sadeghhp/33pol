namespace Pol33.Conformance.Tests;

public sealed class ProjectWiringTests
{
    [Fact]
    public void Assembly_Loads()
    {
        typeof(Pol33.Core.Errors.GatewayErrorCode).Assembly.GetName().Name.Should().Be("33pol.Core");
    }
}
