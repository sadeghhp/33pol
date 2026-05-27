using System.Text.RegularExpressions;

namespace Pol33.Core.Providers;

public static partial class EnvVarNameValidator
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ValidNamePattern();

    public static bool TryValidate(string? envVar, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(envVar))
        {
            error = "envVar is required.";
            return false;
        }

        var trimmed = envVar.Trim();
        if (LooksLikeSecret(trimmed))
        {
            error =
                "envVar must be the environment variable name on the gateway (e.g. OPENROUTER_API_KEY), not the API key secret.";
            return false;
        }

        if (!ValidNamePattern().IsMatch(trimmed))
        {
            error =
                "envVar must be a valid environment variable name (letters, digits, underscore; cannot start with a digit).";
            return false;
        }

        normalized = trimmed;
        return true;
    }

    internal static bool LooksLikeSecret(string value)
    {
        if (value.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Heuristic: raw API tokens are typically long single tokens without underscores.
        if (value.Length >= 32 && !value.Contains('_'))
        {
            return true;
        }

        return false;
    }
}
