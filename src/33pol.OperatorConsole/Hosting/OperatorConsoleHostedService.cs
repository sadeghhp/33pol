using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.OperatorConsole.Commands;
using Spectre.Console;

namespace Pol33.OperatorConsole.Hosting;

public sealed class OperatorConsoleHostedService(
    IControlPlaneCommands commands,
    IOptions<OperatorConsoleOptions> options,
    ILogger<OperatorConsoleHostedService> logger) : BackgroundService
{
    private readonly ModelRegistryConsoleInteractor _registryConsole = new(commands);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        if (Console.IsInputRedirected)
        {
            logger.LogInformation("Operator console disabled: stdin is redirected.");
            return;
        }

        AnsiConsole.MarkupLine("[grey]33pol operator console — type 'help' for commands.[/]");
        while (!stoppingToken.IsCancellationRequested)
        {
            var line = AnsiConsole.Ask<string>("[green]33pol>[/]");
            var intent = ConsoleCommandParser.Parse(line);
            if (intent.Kind == ConsoleCommandKind.Exit)
            {
                break;
            }

            try
            {
                await ExecuteIntentAsync(intent, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Operator command failed.");
                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    private async Task ExecuteIntentAsync(ConsoleCommandIntent intent, CancellationToken cancellationToken)
    {
        switch (intent.Kind)
        {
            case ConsoleCommandKind.Help:
                AnsiConsole.MarkupLine(
                    "[yellow]Commands:[/] help, exit, status, summary, watch summary, backends, " +
                    "requests [--limit N], reload, models list|add|edit <id>|remove <id>");
                break;
            case ConsoleCommandKind.Status:
            case ConsoleCommandKind.Summary:
                RenderSummary(commands.GetSummary());
                break;
            case ConsoleCommandKind.WatchSummary:
                while (!cancellationToken.IsCancellationRequested)
                {
                    AnsiConsole.Clear();
                    RenderSummary(commands.GetSummary());
                    await Task.Delay(options.Value.RefreshIntervalMs, cancellationToken).ConfigureAwait(false);
                }

                break;
            case ConsoleCommandKind.Backends:
                RenderBackends(commands.ListBackends());
                break;
            case ConsoleCommandKind.Requests:
                RenderRequests(commands.ListRecentRequests(intent.Limit));
                break;
            case ConsoleCommandKind.Reload:
                var reload = await commands.ReloadConfigAsync(cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[cyan]Reload:[/] {Markup.Escape(reload.Status)}");
                break;
            case ConsoleCommandKind.ModelsList:
                RenderModels(commands.ListModels());
                break;
            case ConsoleCommandKind.ModelsAdd:
                await _registryConsole.AddModelAsync(cancellationToken).ConfigureAwait(false);
                break;
            case ConsoleCommandKind.ModelsEdit:
                if (string.IsNullOrWhiteSpace(intent.ModelId))
                {
                    AnsiConsole.MarkupLine("[red]Usage: models edit <id>[/]");
                    break;
                }

                await _registryConsole.EditModelAsync(intent.ModelId, cancellationToken).ConfigureAwait(false);
                break;
            case ConsoleCommandKind.ModelsRemove:
                if (string.IsNullOrWhiteSpace(intent.ModelId))
                {
                    AnsiConsole.MarkupLine("[red]Usage: models remove <id>[/]");
                    break;
                }

                await _registryConsole.RemoveModelAsync(intent.ModelId, cancellationToken).ConfigureAwait(false);
                break;
            default:
                AnsiConsole.MarkupLine("[red]Unknown command.[/] Type 'help'.");
                break;
        }
    }

    private static void RenderSummary(Core.Models.AdminSummarySnapshot summary)
    {
        AnsiConsole.Write(BuildSummaryTable(summary));
    }

    private static Table BuildSummaryTable(Core.Models.AdminSummarySnapshot summary)
    {
        var table = new Table().Title("Gateway summary");
        table.AddColumn("Metric");
        table.AddColumn("Value");
        table.AddRow("Uptime", summary.Uptime);
        table.AddRow("Total requests", summary.TotalInferenceRequests.ToString());
        table.AddRow("Errors", summary.TotalErrors.ToString());
        table.AddRow("Avg latency (ms)", summary.AverageLatencyMs.ToString("F1"));
        table.AddRow("Active streams", summary.ActiveStreams.ToString());
        table.AddRow("Rate limit rejections", summary.RateLimitRejections.ToString());
        table.AddRow("Quota rejections", summary.QuotaRejections.ToString());
        return table;
    }

    private static void RenderModels(IReadOnlyList<Core.Models.ModelConfig> models)
    {
        var table = new Table().Title("Models");
        table.AddColumn("Id");
        table.AddColumn("URL");
        table.AddColumn("Aliases");
        foreach (var model in models)
        {
            table.AddRow(
                model.Id,
                model.Url,
                model.Aliases.Count > 0 ? string.Join(", ", model.Aliases) : "-");
        }

        AnsiConsole.Write(table);
    }

    private static void RenderBackends(IReadOnlyList<Core.Models.BackendAdminDto> backends)
    {
        var table = new Table().Title("Backends");
        table.AddColumn("Model");
        table.AddColumn("URL");
        table.AddColumn("Healthy");
        foreach (var backend in backends)
        {
            table.AddRow(backend.ModelId, backend.Url, backend.IsHealthy ? "yes" : "no");
        }

        AnsiConsole.Write(table);
    }

    private static void RenderRequests(IReadOnlyList<Core.Models.RecentRequestEntry> requests)
    {
        var table = new Table().Title("Recent requests");
        table.AddColumn("Time");
        table.AddColumn("Model");
        table.AddColumn("Status");
        table.AddColumn("Duration");
        foreach (var request in requests)
        {
            table.AddRow(
                request.TimestampUtc.ToString("O"),
                request.ModelId ?? "-",
                request.StatusCode.ToString(),
                $"{request.DurationMs:F0}ms");
        }

        AnsiConsole.Write(table);
    }
}
