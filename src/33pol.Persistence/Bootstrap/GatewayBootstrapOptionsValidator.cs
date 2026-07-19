using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pol33.Core.Security;

namespace Pol33.Persistence.Bootstrap;

/// <summary>
/// Fails startup outside Development when the bootstrap secrets that seed and hash the admin API key
/// are empty, a published default, too short, or inconsistent with the runtime verification pepper.
/// The admin key is validated only when supplied — an already-seeded database does not require it.
/// </summary>
public sealed class GatewayBootstrapOptionsValidator : IValidateOptions<GatewayBootstrapOptions>
{
    // The section is owned by Pol33.Security (GatewaySecurityOptions.SectionName); referenced here as a
    // literal to avoid a Persistence -> Security project dependency (Security already depends on Persistence).
    private const string SecurityPepperKey = "Gateway:Security:KeyPepper";

    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public GatewayBootstrapOptionsValidator(IHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    public ValidateOptionsResult Validate(string? name, GatewayBootstrapOptions options)
    {
        if (_environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        var pepper = options.KeyPepper?.Trim();
        if (string.IsNullOrEmpty(pepper)
            || WellKnownWeakSecrets.IsWeakPepper(pepper)
            || pepper.Length < GatewayBootstrapOptions.MinimumPepperLength)
        {
            return ValidateOptionsResult.Fail(
                $"{GatewayBootstrapOptions.SectionName}:KeyPepper must be set to a strong, non-default "
                + $"value of at least {GatewayBootstrapOptions.MinimumPepperLength} characters outside Development. "
                + "Set the GATEWAY_KEY_PEPPER environment variable to a freshly generated secret.");
        }

        // The bootstrap pepper hashes the admin key at seed time; the security pepper verifies keys at
        // runtime. If they diverge the bootstrapped admin key silently never authenticates, so require a match.
        var securityPepper = _configuration[SecurityPepperKey]?.Trim();
        if (!string.IsNullOrEmpty(securityPepper) && !string.Equals(pepper, securityPepper, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                $"{GatewayBootstrapOptions.SectionName}:KeyPepper and {SecurityPepperKey} must be identical; "
                + "otherwise the bootstrapped admin key is hashed with one pepper and verified with another and "
                + "can never authenticate. Set both from the same GATEWAY_KEY_PEPPER value.");
        }

        var adminKey = options.AdminApiKey?.Trim();
        if (!string.IsNullOrEmpty(adminKey))
        {
            if (WellKnownWeakSecrets.IsWeakAdminApiKey(adminKey)
                || adminKey.Length < GatewayBootstrapOptions.MinimumAdminKeyLength)
            {
                return ValidateOptionsResult.Fail(
                    $"{GatewayBootstrapOptions.SectionName}:AdminApiKey must be a strong, non-default value of at "
                    + $"least {GatewayBootstrapOptions.MinimumAdminKeyLength} characters outside Development. "
                    + "Set the GATEWAY_ADMIN_API_KEY environment variable to a freshly generated secret.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
