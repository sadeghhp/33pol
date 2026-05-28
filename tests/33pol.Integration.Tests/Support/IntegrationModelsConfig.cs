namespace Pol33.Integration.Tests.Support;

internal static class IntegrationModelsConfig
{
    internal static string WriteStandardModelsConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"33pol-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "models.json");
        const string json = """
            {
              "models": [
                {
                  "id": "local-mock",
                  "url": "http://127.0.0.1:18080",
                  "maxContextLength": 8192,
                  "aliases": ["mock", "gpt-local"]
                },
                {
                  "id": "other-mock",
                  "url": "http://127.0.0.1:18080",
                  "maxContextLength": 8192,
                  "aliases": []
                }
              ]
            }
            """;
        File.WriteAllText(path, json);
        return path;
    }

    internal static void ApplyStandardModelsSettings(IDictionary<string, string?> settings, string configPath)
    {
        settings["Gateway:ModelsConfigPath"] = configPath;
        settings["Gateway:RegistryWatchEnabled"] = "false";
    }
}
