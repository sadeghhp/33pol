namespace Pol33.Core.Providers;

public static class ProviderCatalog
{
    public const string CustomProviderId = "custom";

    private static readonly ProviderDefinition[] BuiltIn =
    [
        new(
            "openrouter",
            "OpenRouter",
            "https://openrouter.ai/api",
            "https://openrouter.ai/api/v1/models",
            "OPENROUTER_API_KEY"),
        new(
            "openai",
            "OpenAI",
            "https://api.openai.com",
            "https://api.openai.com/v1/models",
            "OPENAI_API_KEY"),
        new(
            "together",
            "Together AI",
            "https://api.together.xyz",
            "https://api.together.xyz/v1/models",
            "TOGETHER_API_KEY"),
        new(
            "groq",
            "Groq",
            "https://api.groq.com/openai",
            "https://api.groq.com/openai/v1/models",
            "GROQ_API_KEY"),
        new(
            "deepseek",
            "DeepSeek",
            "https://api.deepseek.com",
            "https://api.deepseek.com/v1/models",
            "DEEPSEEK_API_KEY"),
        new(
            "mistral",
            "Mistral",
            "https://api.mistral.ai",
            "https://api.mistral.ai/v1/models",
            "MISTRAL_API_KEY"),
        new(
            "fireworks",
            "Fireworks AI",
            "https://api.fireworks.ai/inference/v1",
            "https://api.fireworks.ai/inference/v1/models",
            "FIREWORKS_API_KEY"),
        // No discovery URL: host.docker.internal resolves to a private address, which the guarded
        // discovery client rightly refuses. See ProviderDefinition.SupportsDiscovery.
        new(
            "lmstudio",
            "LM Studio (local)",
            "http://host.docker.internal:1234",
            string.Empty,
            string.Empty,
            RequiresUpstreamAuth: false),
        new(
            "dashscope",
            "Alibaba Model Studio (DashScope, intl)",
            "https://dashscope-intl.aliyuncs.com/compatible-mode",
            "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/models",
            "DASHSCOPE_API_KEY"),
    ];

    public static IReadOnlyList<ProviderDefinition> ListBuiltIn() => BuiltIn;

    public static bool TryGetBuiltIn(string providerId, out ProviderDefinition? definition)
    {
        definition = BuiltIn.FirstOrDefault(
            p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        return definition is not null;
    }
}
