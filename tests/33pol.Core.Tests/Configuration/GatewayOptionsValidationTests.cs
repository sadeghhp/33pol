using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Pol33.Core.Configuration;

namespace Pol33.Core.Tests.Configuration;

public sealed class GatewayOptionsValidationTests
{
    [Fact]
    public void Validate_EmptyModelsConfigPath_ReturnsError()
    {
        var options = new GatewayOptions { ModelsConfigPath = "  " };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().Contain(e => e.Contains(nameof(GatewayOptions.ModelsConfigPath), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NegativeReloadInterval_ReturnsError()
    {
        var options = new GatewayOptions { ConfigReloadIntervalSeconds = -1 };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().Contain(e => e.Contains(nameof(GatewayOptions.ConfigReloadIntervalSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReloadIntervalAbove300_ReturnsError()
    {
        var options = new GatewayOptions { ConfigReloadIntervalSeconds = 301 };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().Contain(e => e.Contains(nameof(GatewayOptions.ConfigReloadIntervalSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Default_ConfigReloadInterval_IsTwoSeconds()
    {
        new GatewayOptions().ConfigReloadIntervalSeconds.Should().Be(2);
    }

    [Fact]
    public void Validate_ZeroHealthCheckInterval_ReturnsError()
    {
        var options = new GatewayOptions { HealthCheckIntervalSeconds = 0 };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().Contain(e => e.Contains(nameof(GatewayOptions.HealthCheckIntervalSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ValidOptions_ReturnsNoErrors()
    {
        var options = new GatewayOptions();

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Bind_FromConfigurationDictionary_BindsGatewaySection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:ModelsConfigPath"] = "/etc/33pol/models.json",
                ["Gateway:ConfigReloadIntervalSeconds"] = "60",
            })
            .Build();

        var options = new GatewayOptions();
        configuration.GetSection(GatewayOptions.SectionName).Bind(options);

        options.ModelsConfigPath.Should().Be("/etc/33pol/models.json");
        options.ConfigReloadIntervalSeconds.Should().Be(60);
    }
}
