namespace Pol33.Observability.Tests;

public sealed class ProjectWiringTests
{
    [Fact]
    public void Assembly_Loads()
    {
        typeof(Pol33.Observability.Runtime.GatewayRuntimeState).Assembly.GetName().Name.Should().Be("33pol.Observability");
    }
}
