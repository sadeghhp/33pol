namespace Pol33.Core.Configuration;

public static class GatewayOptionsValidation
{
    public static IReadOnlyList<string> Validate(GatewayOptions options, bool isProduction = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ModelsConfigPath))
        {
            errors.Add($"{nameof(GatewayOptions.ModelsConfigPath)} must be a non-empty path.");
        }

        if (options.ConfigReloadIntervalSeconds is < 0 or > 300)
        {
            errors.Add($"{nameof(GatewayOptions.ConfigReloadIntervalSeconds)} must be between 0 and 300 seconds.");
        }

        if (options.HealthCheckIntervalSeconds < 1)
        {
            errors.Add($"{nameof(GatewayOptions.HealthCheckIntervalSeconds)} must be at least 1 second.");
        }

        if (isProduction && options.RequireApiKeysInProduction && !options.IsAuthenticationEnabled)
        {
            errors.Add("At least one inference or admin API key must be configured in Production.");
        }

        return errors;
    }

    public static bool IsValid(GatewayOptions options, out IReadOnlyList<string> errors, bool isProduction = false)
    {
        errors = Validate(options, isProduction);
        return errors.Count == 0;
    }
}
