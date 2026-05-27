using Pol33.OperatorConsole.Commands;

namespace Pol33.OperatorConsole.Tests.Commands;

public sealed class ConsoleCommandParserTests
{
    [Theory]
    [InlineData("help", ConsoleCommandKind.Help)]
    [InlineData("summary", ConsoleCommandKind.Summary)]
    [InlineData("watch summary", ConsoleCommandKind.WatchSummary)]
    [InlineData("requests --limit 10", ConsoleCommandKind.Requests)]
    [InlineData("keys list", ConsoleCommandKind.KeysList)]
    public void Parse_KnownCommands_ReturnsExpectedKind(string input, ConsoleCommandKind expected)
    {
        ConsoleCommandParser.Parse(input).Kind.Should().Be(expected);
    }

    [Fact]
    public void Parse_Requests_ParsesLimit()
    {
        ConsoleCommandParser.Parse("requests --limit 25").Limit.Should().Be(25);
    }

    [Theory]
    [InlineData("models add", ConsoleCommandKind.ModelsAdd)]
    [InlineData("models edit my-model", ConsoleCommandKind.ModelsEdit, "my-model")]
    [InlineData("models remove other", ConsoleCommandKind.ModelsRemove, "other")]
    public void Parse_ModelMutations_ReturnsExpectedKind(string input, ConsoleCommandKind expected, string? modelId = null)
    {
        var intent = ConsoleCommandParser.Parse(input);
        intent.Kind.Should().Be(expected);
        intent.ModelId.Should().Be(modelId);
    }
}
