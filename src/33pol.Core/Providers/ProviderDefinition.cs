namespace Pol33.Core.Providers;

/// <summary>
/// Built-in OpenAI-compatible upstream used for model discovery and registry templates.
/// </summary>
/// <param name="ModelsListUrl">
/// The provider's <c>/v1/models</c> endpoint, or empty when built-in discovery is not available
/// for this provider (see <see cref="SupportsDiscovery"/>).
/// </param>
public sealed record ProviderDefinition(
    string Id,
    string DisplayName,
    string UpstreamBaseUrl,
    string ModelsListUrl,
    string DefaultEnvVar,
    bool RequiresUpstreamAuth = true)
{
    /// <summary>
    /// Whether the gateway can list this provider's models itself. False for local providers: their
    /// endpoint lives on the operator's own host (<c>host.docker.internal</c>, a LAN address), which
    /// always resolves to a private address and is therefore rejected by the SSRF guard on the
    /// discovery client — so offering discovery only produced a misleading "blocked address" error.
    /// The upstream base URL is still a valid registry template; models are added by id.
    /// </summary>
    public bool SupportsDiscovery => !string.IsNullOrWhiteSpace(ModelsListUrl);
}
