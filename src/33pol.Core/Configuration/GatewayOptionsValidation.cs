namespace Pol33.Core.Configuration;

public static class GatewayOptionsValidation
{
    public static IReadOnlyList<string> Validate(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ModelsConfigPath))
        {
            errors.Add($"{nameof(GatewayOptions.ModelsConfigPath)} must be a non-empty path.");
        }

        if (options.ConfigReloadIntervalSeconds < 0)
        {
            errors.Add($"{nameof(GatewayOptions.ConfigReloadIntervalSeconds)} must be zero or positive.");
        }

        return errors;
    }

    public static bool IsValid(GatewayOptions options, out IReadOnlyList<string> errors)
    {
        errors = Validate(options);
        return errors.Count == 0;
    }
}
