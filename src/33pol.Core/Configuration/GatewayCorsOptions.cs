namespace Pol33.Core.Configuration;

public sealed class GatewayCorsOptions
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}
