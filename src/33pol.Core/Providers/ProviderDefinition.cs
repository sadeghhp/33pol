namespace Pol33.Core.Providers;

/// <summary>
/// Built-in OpenAI-compatible upstream used for model discovery and registry templates.
/// </summary>
public sealed record ProviderDefinition(
    string Id,
    string DisplayName,
    string UpstreamBaseUrl,
    string ModelsListUrl,
    string DefaultEnvVar,
    bool RequiresUpstreamAuth = true);
