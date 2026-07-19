using System.Collections;
using Pol33.Core.Configuration;

namespace Pol33.Core.Tests.Configuration;

public sealed class GatewayCorsEnvironmentConfigurationTests : IDisposable
{
    private readonly Dictionary<string, string?> _previousValues = new(StringComparer.Ordinal);

    [Fact]
    public void ReadAllowedOriginsFromEnvironment_IndexedVars_ReturnsSortedNormalizedOrigins()
    {
        SetEnv("GATEWAY_CORS_ALLOWED_ORIGIN_2", "http://localhost");
        SetEnv("GATEWAY_CORS_ALLOWED_ORIGIN_0", "https://*.github.io");
        SetEnv("GATEWAY_CORS_ALLOWED_ORIGIN_1", "http://localhost:3000/");

        var result = GatewayCorsEnvironmentConfiguration.ReadAllowedOriginsFromEnvironment();

        result.Should().Equal(
        [
            "https://*.github.io",
            "http://localhost:3000",
            "http://localhost",
        ]);
    }

    [Fact]
    public void ReadAllowedOriginsFromEnvironment_CommaSeparatedVar_MergesWithIndexedVars()
    {
        SetEnv("GATEWAY_CORS_ALLOWED_ORIGINS", "https://app.example.com, http://localhost:5173");
        SetEnv("GATEWAY_CORS_ALLOWED_ORIGIN_0", "https://*.github.io");

        var result = GatewayCorsEnvironmentConfiguration.ReadAllowedOriginsFromEnvironment();

        result.Should().Equal(
        [
            "https://app.example.com",
            "http://localhost:5173",
            "https://*.github.io",
        ]);
    }

    [Fact]
    public void ReadAllowedOriginsFromEnvironment_NoVars_ReturnsEmpty()
    {
        ClearGatewayCorsEnv();

        GatewayCorsEnvironmentConfiguration.ReadAllowedOriginsFromEnvironment().Should().BeEmpty();
    }

    [Fact]
    public void ReadAllowedOriginsFromEnvironment_IgnoresBlankIndexedValues()
    {
        SetEnv("GATEWAY_CORS_ALLOWED_ORIGIN_0", "https://ok.example.com");
        SetEnv("GATEWAY_CORS_ALLOWED_ORIGIN_1", "   ");

        GatewayCorsEnvironmentConfiguration.ReadAllowedOriginsFromEnvironment()
            .Should().Equal(["https://ok.example.com"]);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _previousValues)
        {
            if (value is null)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
            else
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private void SetEnv(string key, string? value)
    {
        if (!_previousValues.ContainsKey(key))
        {
            _previousValues[key] = Environment.GetEnvironmentVariable(key);
        }

        Environment.SetEnvironmentVariable(key, value);
    }

    private static void ClearGatewayCorsEnv()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (key is null)
            {
                continue;
            }

            if (key.Equals(GatewayCorsEnvironmentConfiguration.AllowedOriginsEnvVar, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(GatewayCorsEnvironmentConfiguration.AllowedOriginPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }
    }
}
