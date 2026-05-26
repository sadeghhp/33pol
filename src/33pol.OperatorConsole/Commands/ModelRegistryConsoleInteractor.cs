using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Spectre.Console;

namespace Pol33.OperatorConsole.Commands;

public sealed class ModelRegistryConsoleInteractor(IControlPlaneCommands commands)
{
    public async Task AddModelAsync(CancellationToken cancellationToken)
    {
        var id = AnsiConsole.Ask<string>("Model id:");
        var url = AnsiConsole.Ask<string>("Backend URL:");
        var aliasesRaw = AnsiConsole.Ask<string>("Aliases (comma-separated, optional):", string.Empty);
        var aliases = ParseAliases(aliasesRaw);

        var model = new ModelConfig { Id = id, Url = url, Aliases = aliases };
        var result = await commands.AddModelAsync(model, cancellationToken).ConfigureAwait(false);
        RenderMutationResult(result);
    }

    public async Task EditModelAsync(string modelId, CancellationToken cancellationToken)
    {
        var url = AnsiConsole.Ask<string>($"New URL for [cyan]{Markup.Escape(modelId)}[/]:");
        var aliasesRaw = AnsiConsole.Ask<string>("Aliases (comma-separated, empty to keep unchanged):", string.Empty);
        var model = new ModelConfig { Id = modelId, Url = url };
        if (!string.IsNullOrWhiteSpace(aliasesRaw))
        {
            model.Aliases = ParseAliases(aliasesRaw);
        }

        var result = await commands.UpdateModelAsync(modelId, model, cancellationToken).ConfigureAwait(false);
        RenderMutationResult(result);
    }

    public async Task RemoveModelAsync(string modelId, CancellationToken cancellationToken)
    {
        if (!AnsiConsole.Confirm($"Remove model [yellow]{Markup.Escape(modelId)}[/]?"))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return;
        }

        var result = await commands.RemoveModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        RenderMutationResult(result);
    }

    private static List<string> ParseAliases(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static void RenderMutationResult(RegistryMutationResult result)
    {
        var color = result.Success ? "green" : "red";
        AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(result.Message)}[/] (HTTP {result.SuggestedStatusCode})");
    }
}
