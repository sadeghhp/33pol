namespace Pol33.OperatorConsole.Tests;

public sealed class ProjectWiringTests
{
    [Fact]
    public void Assembly_Loads()
    {
        typeof(Pol33.OperatorConsole.Commands.ConsoleCommandParser).Assembly.GetName().Name.Should().Be("33pol.OperatorConsole");
    }
}
