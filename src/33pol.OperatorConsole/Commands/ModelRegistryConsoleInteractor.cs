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
        var current = FindModel(commands.ListModels(), modelId);
        if (current is null)
        {
            AnsiConsole.MarkupLine($"[red]Model '{Markup.Escape(modelId)}' not found.[/]");
            return;
        }

        var url = AnsiConsole.Ask<string>($"New URL for [cyan]{Markup.Escape(current.Id)}[/]:");
        var aliasesRaw = AnsiConsole.Ask<string>("Aliases (comma-separated, empty to keep unchanged):", string.Empty);
        var model = BuildEditedModel(current, url, aliasesRaw);

        var result = await commands.UpdateModelAsync(current.Id, model, cancellationToken).ConfigureAwait(false);
        RenderMutationResult(result);
    }

    /// <summary>
    /// Produces the merged model passed to <see cref="IControlPlaneCommands.UpdateModelAsync"/>. The
    /// registry writer does a full replace, not a merge, so every field the operator was not asked
    /// about (upstream auth, capabilities, public access, context length, model type) must be carried
    /// over from the current model; only <see cref="ModelConfig.Url"/> is overwritten, and
    /// <see cref="ModelConfig.Aliases"/> only when the operator typed a non-empty value.
    /// </summary>
    public static ModelConfig BuildEditedModel(ModelConfig current, string url, string aliasesRaw)
    {
        var model = CloneModel(current);
        model.Url = url;
        if (!string.IsNullOrWhiteSpace(aliasesRaw))
        {
            model.Aliases = ParseAliases(aliasesRaw);
        }

        return model;
    }

    public static ModelConfig? FindModel(IReadOnlyList<ModelConfig> models, string modelId)
    {
        var trimmed = modelId.Trim();
        foreach (var model in models)
        {
            if (string.Equals(model.Id, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        foreach (var model in models)
        {
            if (model.Aliases.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return null;
    }

    private static ModelConfig CloneModel(ModelConfig model) =>
        new()
        {
            Id = model.Id,
            Url = model.Url,
            UpstreamAuth = model.UpstreamAuth is null
                ? null
                : new UpstreamAuthConfig
                {
                    Type = model.UpstreamAuth.Type,
                    EnvVar = model.UpstreamAuth.EnvVar,
                    SecretRef = model.UpstreamAuth.SecretRef,
                },
            MaxContextLength = model.MaxContextLength,
            Aliases = [.. model.Aliases],
            PublicAccess = model.PublicAccess,
            Capabilities = [.. model.Capabilities],
            ModelType = model.ModelType,
        };

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
