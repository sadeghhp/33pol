namespace Pol33.OperatorConsole.Commands;

public enum ConsoleCommandKind
{
    Unknown,
    Help,
    Exit,
    Status,
    Summary,
    WatchSummary,
    Backends,
    Requests,
    Reload,
    ModelsList,
    ModelsAdd,
    ModelsEdit,
    ModelsRemove,
}

public sealed record ConsoleCommandIntent(
    ConsoleCommandKind Kind,
    int Limit = 50,
    string? ModelId = null);

public static class ConsoleCommandParser
{
    public static ConsoleCommandIntent Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new ConsoleCommandIntent(ConsoleCommandKind.Unknown);
        }

        var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts[0].ToLowerInvariant();

        return verb switch
        {
            "help" or "?" => new ConsoleCommandIntent(ConsoleCommandKind.Help),
            "exit" or "quit" => new ConsoleCommandIntent(ConsoleCommandKind.Exit),
            "status" => new ConsoleCommandIntent(ConsoleCommandKind.Status),
            "summary" => new ConsoleCommandIntent(ConsoleCommandKind.Summary),
            "watch" when parts.Length > 1 && parts[1].Equals("summary", StringComparison.OrdinalIgnoreCase)
                => new ConsoleCommandIntent(ConsoleCommandKind.WatchSummary),
            "backends" => new ConsoleCommandIntent(ConsoleCommandKind.Backends),
            "requests" => new ConsoleCommandIntent(ConsoleCommandKind.Requests, ParseLimit(parts)),
            "reload" => new ConsoleCommandIntent(ConsoleCommandKind.Reload),
            "models" when parts.Length > 1 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase)
                => new ConsoleCommandIntent(ConsoleCommandKind.ModelsList),
            "models" when parts.Length > 1 && parts[1].Equals("add", StringComparison.OrdinalIgnoreCase)
                => new ConsoleCommandIntent(ConsoleCommandKind.ModelsAdd),
            "models" when parts.Length > 2 && parts[1].Equals("edit", StringComparison.OrdinalIgnoreCase)
                => new ConsoleCommandIntent(ConsoleCommandKind.ModelsEdit, ModelId: parts[2]),
            "models" when parts.Length > 2 && parts[1].Equals("remove", StringComparison.OrdinalIgnoreCase)
                => new ConsoleCommandIntent(ConsoleCommandKind.ModelsRemove, ModelId: parts[2]),
            _ => new ConsoleCommandIntent(ConsoleCommandKind.Unknown),
        };
    }

    private static int ParseLimit(string[] parts)
    {
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("--limit", StringComparison.OrdinalIgnoreCase))
            {
                var value = parts[i].Contains('=')
                    ? parts[i].Split('=')[1]
                    : i + 1 < parts.Length ? parts[i + 1] : "50";
                return int.TryParse(value, out var limit) ? Math.Clamp(limit, 1, 500) : 50;
            }
        }

        return 50;
    }
}
