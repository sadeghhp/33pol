using Pol33.OperatorConsole.Commands;

namespace Pol33.OperatorConsole.Tests.Commands;

public sealed class ConsoleCommandParserTests
{
    [Theory]
    [InlineData("help", ConsoleCommandKind.Help)]
    [InlineData("summary", ConsoleCommandKind.Summary)]
    [InlineData("watch summary", ConsoleCommandKind.WatchSummary)]
    [InlineData("requests --limit 10", ConsoleCommandKind.Requests)]
    public void Parse_KnownCommands_ReturnsExpectedKind(string input, ConsoleCommandKind expected)
    {
        ConsoleCommandParser.Parse(input).Kind.Should().Be(expected);
    }

    [Fact]
    public void Parse_Requests_ParsesLimit()
    {
        ConsoleCommandParser.Parse("requests --limit 25").Limit.Should().Be(25);
    }
}
