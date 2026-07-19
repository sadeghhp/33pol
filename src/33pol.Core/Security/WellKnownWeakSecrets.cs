namespace Pol33.Core.Security;

/// <summary>
/// Secrets that shipped as development/sample defaults in this repository (compose files, .env
/// examples, appsettings). They are public by definition, so a Production deployment must never
/// run with any of them. Startup validators reject these values outside Development, which also
/// protects operators who copied an old default into their <c>.env</c> before it was removed.
/// </summary>
public static class WellKnownWeakSecrets
{
    /// <summary>Key peppers that have been published as defaults. Compared case-sensitively after trimming.</summary>
    public static readonly IReadOnlyCollection<string> Peppers = new HashSet<string>(StringComparer.Ordinal)
    {
        "dev-pepper-change-me",
        // Shipped as the docker-compose / .env.example fallback pepper before secrets hardening.
        "oJHJdzSvNdVFbFd8fDrexL3bf6n9ggW",
    };

    /// <summary>Admin API keys that have been published as defaults. Compared case-sensitively after trimming.</summary>
    public static readonly IReadOnlyCollection<string> AdminApiKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        // Shipped as the docker-compose / .env.example fallback admin key before secrets hardening.
        "sk-33pol-4aa283ddb877adaccc60cb53314fa15cfd41f01084df064c",
        // Obvious local-dev sentinel used by bare `docker compose up`.
        "sk-33pol-dev-local-unsafe",
    };

    public static bool IsWeakPepper(string? value) =>
        value is not null && Peppers.Contains(value.Trim());

    public static bool IsWeakAdminApiKey(string? value) =>
        value is not null && AdminApiKeys.Contains(value.Trim());
}
