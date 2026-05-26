namespace Pol33.Proxy.Tests;

public sealed class ProjectWiringTests
{
    [Fact]
    public void Assembly_Loads()
    {
        typeof(Pol33.Proxy.Middleware.ModelRouterMiddleware).Assembly.GetName().Name.Should().Be("33pol.Proxy");
    }
}
