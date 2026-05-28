namespace Pol33.Core.Configuration;

public sealed class GatewayCorsOptions
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Trims entries, removes trailing slashes, and drops blanks.</summary>
    public static string[] NormalizeOrigins(IEnumerable<string>? origins)
    {
        if (origins is null)
        {
            return [];
        }

        return origins
            .Select(static o => o.Trim())
            .Where(static o => o.Length > 0)
            .Select(static o => o.TrimEnd('/'))
            .Where(static o => o.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public string[] GetNormalizedOrigins() => NormalizeOrigins(AllowedOrigins);
}
