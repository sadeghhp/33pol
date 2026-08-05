using Pol33.Core.Configuration;

namespace Pol33.Core.Providers;

/// <summary>
/// Decides which environment variables the gateway is willing to read an upstream bearer token from.
/// </summary>
/// <remarks>
/// Both the provider-discovery endpoint and a model's <c>upstreamAuth.envVar</c> resolve a name the
/// caller supplies and send the resulting value to a URL the caller also supplies. Without a
/// restriction that turns an admin credential into "read any secret in the gateway's environment and
/// post it anywhere" — the database connection string, the key pepper, cloud credentials. The name is
/// therefore checked against a policy rather than passed straight to the configuration lookup.
///
/// <para>The default policy accepts the built-in providers' variables plus the documented
/// <c>*_API_KEY</c> / <c>*_TOKEN</c> convention for self-hosted upstreams, and always refuses names
/// that address the gateway's own secrets. Operators whose variable does not fit the convention add
/// it to <c>Gateway:UpstreamEnvVarAllowList</c>.</para>
/// </remarks>
public sealed class UpstreamEnvVarPolicy
{
    public const string AllowListSettingKey = "Gateway:UpstreamEnvVarAllowList";

    private static readonly string[] DeniedPrefixes =
    [
        "GATEWAY", "POL33", "ASPNETCORE", "DOTNET", "CONNECTIONSTRINGS",
    ];

    private static readonly string[] DeniedFragments =
    [
        "PEPPER", "PASSWORD", "CONNECTIONSTRING", "PRIVATE_KEY", "SECRET_ACCESS", "SESSION_TOKEN",
    ];

    private static readonly string[] AllowedSuffixes =
    [
        "_API_KEY", "_APIKEY", "_KEY", "_TOKEN",
    ];

    private readonly HashSet<string> _allowList;

    public UpstreamEnvVarPolicy(IEnumerable<string>? configuredAllowList = null)
    {
        _allowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in ProviderCatalog.ListBuiltIn())
        {
            if (!string.IsNullOrWhiteSpace(provider.DefaultEnvVar))
            {
                _allowList.Add(provider.DefaultEnvVar.Trim());
            }
        }

        foreach (var name in configuredAllowList ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _allowList.Add(name.Trim());
            }
        }
    }

    public static UpstreamEnvVarPolicy FromOptions(GatewayOptions options) =>
        new(options?.UpstreamEnvVarAllowList);

    /// <summary>
    /// Whether the gateway may read <paramref name="envVar"/>. The name must already be a
    /// syntactically valid variable name (see <see cref="EnvVarNameValidator"/>).
    /// </summary>
    public bool IsAllowed(string? envVar, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(envVar))
        {
            error = "envVar is required.";
            return false;
        }

        var name = envVar.Trim();

        if (_allowList.Contains(name))
        {
            return true;
        }

        var upper = name.ToUpperInvariant();

        if (DeniedPrefixes.Any(prefix => upper.StartsWith(prefix, StringComparison.Ordinal)) ||
            DeniedFragments.Any(fragment => upper.Contains(fragment, StringComparison.Ordinal)))
        {
            error = Refusal(name);
            return false;
        }

        if (AllowedSuffixes.Any(suffix => upper.EndsWith(suffix, StringComparison.Ordinal)))
        {
            return true;
        }

        error = Refusal(name);
        return false;
    }

    private static string Refusal(string name) =>
        $"The gateway will not read the environment variable '{name}' as an upstream credential. " +
        "Use a variable named for the upstream it belongs to (for example MY_UPSTREAM_API_KEY), " +
        $"or add this name to {AllowListSettingKey}.";
}
