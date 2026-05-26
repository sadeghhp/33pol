namespace Pol33.Core.Models;

public sealed class ConfigStatusResponse
{
    public bool HotReloadEnabled { get; init; }

    public bool WatchEnabled { get; init; }

    public DateTimeOffset? LastReload { get; init; }

    public int ModelCount { get; init; }

    public IReadOnlyList<ConfigStatusModel> Models { get; init; } = [];
}

public sealed class ConfigStatusModel
{
    public required string Id { get; init; }

    public required string Url { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];
}
