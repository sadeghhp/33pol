using System.Collections;
using System.Globalization;

namespace Pol33.Core.Configuration;

/// <summary>
/// Reads Docker-friendly CORS allowlist variables from the process environment.
/// </summary>
public static class GatewayCorsEnvironmentConfiguration
{
    public const string AllowedOriginsEnvVar = "GATEWAY_CORS_ALLOWED_ORIGINS";
    public const string AllowedOriginPrefix = "GATEWAY_CORS_ALLOWED_ORIGIN_";

    public static string[] ReadAllowedOriginsFromEnvironment()
    {
        var combined = new List<string>();

        var listValue = Environment.GetEnvironmentVariable(AllowedOriginsEnvVar);
        if (!string.IsNullOrWhiteSpace(listValue))
        {
            combined.AddRange(
                listValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        var indexed = new SortedDictionary<int, string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (key is null || !key.StartsWith(AllowedOriginPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = key[AllowedOriginPrefix.Length..];
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            var value = entry.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                indexed[index] = value;
            }
        }

        combined.AddRange(indexed.Values);

        return GatewayCorsOptions.NormalizeOrigins(combined);
    }
}
