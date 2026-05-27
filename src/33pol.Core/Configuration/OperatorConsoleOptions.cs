namespace Pol33.Core.Configuration;

public sealed class OperatorConsoleOptions
{
    public const string SectionName = "Gateway:OperatorConsole";

    public bool Enabled { get; set; }

    public int RefreshIntervalMs { get; set; } = 1000;

    /// <summary>Tenant slug for <c>keys list</c> (default bootstrap tenant).</summary>
    public string TenantSlug { get; set; } = "default";
}
