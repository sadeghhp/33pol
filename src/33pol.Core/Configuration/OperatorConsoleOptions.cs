namespace Pol33.Core.Configuration;

public sealed class OperatorConsoleOptions
{
    public const string SectionName = "Gateway:OperatorConsole";

    public bool Enabled { get; set; }

    public int RefreshIntervalMs { get; set; } = 1000;
}
