using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.OperatorConsole.Commands;
using Spectre.Console;

namespace Pol33.OperatorConsole.Commands;

public sealed class OperatorConsoleKeysInteractor(
    IAdminKeyService adminKeys,
    ITenantRepository tenants,
    IOptions<OperatorConsoleOptions> options)
{
    public async Task ListKeysAsync(CancellationToken cancellationToken)
    {
        var slug = options.Value.TenantSlug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            AnsiConsole.MarkupLine("[red]Gateway:OperatorConsole:TenantSlug is not configured.[/]");
            return;
        }

        var tenant = await tenants.GetBySlugAsync(slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            AnsiConsole.MarkupLine($"[red]Tenant '{Markup.Escape(slug)}' not found.[/]");
            return;
        }

        var keys = await adminKeys
            .ListAsync(tenant.Id, includeUsageSummary: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AnsiConsole.Write(BuildKeysTable(keys));
    }

    public static Table BuildKeysTable(IReadOnlyList<AdminApiKeyListItem> keys)
    {
        var table = new Table().Title("API keys (prefixes only)");
        table.AddColumn("Prefix");
        table.AddColumn("Label");
        table.AddColumn("Assignee");
        table.AddColumn("Role");
        table.AddColumn("Created");
        table.AddColumn("Last used");
        table.AddColumn("Status");

        foreach (var key in keys)
        {
            table.AddRow(
                Markup.Escape(key.KeyPrefix),
                Markup.Escape(key.Label ?? "-"),
                Markup.Escape(key.Assignee ?? "-"),
                Markup.Escape(key.Role.ToString()),
                key.CreatedAt.ToString("u"),
                key.LastUsedAt?.ToString("u") ?? "-",
                DescribeStatus(key));
        }

        if (keys.Count == 0)
        {
            table.AddRow("-", "-", "-", "-", "-", "-", "no keys");
        }

        return table;
    }

    /// <summary>
    /// The single word the Status column shows, from the same precedence rules the admin API applies.
    /// </summary>
    public static string DescribeStatus(AdminApiKeyListItem key) =>
        ApiKeyStatus.Describe(key.IsArchived, key.IsRevoked, key.ExpiresAt);
}
