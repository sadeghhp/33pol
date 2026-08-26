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
    public void Validate_ReloadIntervalOutOfRange_ReturnsError()
    {
        var options = new GatewayOptions { ConfigReloadIntervalSeconds = 0 };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().Contain(e => e.Contains(nameof(GatewayOptions.ConfigReloadIntervalSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroHealthCheckInterval_ReturnsError()
    {
        var options = new GatewayOptions { HealthCheckIntervalSeconds = 0 };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().Contain(e => e.Contains(nameof(GatewayOptions.HealthCheckIntervalSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroHealthCheckUnhealthyThreshold_ReturnsError()
    {
        var options = new GatewayOptions { HealthCheckUnhealthyThreshold = 0 };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().Contain(e => e.Contains(nameof(GatewayOptions.HealthCheckUnhealthyThreshold), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroHealthCheckTimeout_ReturnsError()
    {
        var options = new GatewayOptions { HealthCheckTimeoutSeconds = 0 };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().Contain(e => e.Contains(nameof(GatewayOptions.HealthCheckTimeoutSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroCircuitBreakerHalfOpenProbeTimeout_ReturnsError()
    {
        var options = new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { CircuitBreakerHalfOpenProbeTimeoutSeconds = 0 },
        };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().Contain(e => e.Contains(
            nameof(GatewayResilienceOptions.CircuitBreakerHalfOpenProbeTimeoutSeconds),
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroRequestBufferThreshold_ReturnsError()
    {
        var options = new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { RequestBufferThresholdBytes = 0 },
        };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().Contain(e => e.Contains(
            nameof(GatewayResilienceOptions.RequestBufferThresholdBytes),
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ValidOptions_ReturnsNoErrors()
    {
        var options = new GatewayOptions();

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_InvalidResilienceOptions_ReturnsErrors()
    {
        var options = new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions
            {
                ForwardTimeoutSeconds = 0,
                MaxRequestBodyBytes = 0,
                MaxConcurrentForwardsPerModel = 0,
                CircuitBreakerFailureThreshold = 0,
                CircuitBreakerBreakDurationSeconds = 0,
            },
        };

        var errors = GatewayOptionsValidation.Validate(options);

        errors.Should().HaveCountGreaterThanOrEqualTo(5);
    }

    /// <summary>
    /// The breaker window and ratio were never validated: a ratio typo such as 1.5 (or 50 meaning
    /// percent) can never be reached and silently disables the breaker; 0 makes it count-only.
    /// </summary>
    [Theory]
    [InlineData(1.5)]
    [InlineData(50)]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public void Validate_CircuitBreakerFailureRatioOutOfRange_ReturnsError(double ratio)
    {
        var options = new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { CircuitBreakerFailureRatioThreshold = ratio },
        };

        GatewayOptionsValidation.Validate(options)
            .Should().ContainSingle(e => e.Contains(nameof(GatewayResilienceOptions.CircuitBreakerFailureRatioThreshold), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Validate_CircuitBreakerFailureRatioInRange_IsAccepted(double ratio)
    {
        var options = new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { CircuitBreakerFailureRatioThreshold = ratio },
        };

        GatewayOptionsValidation.Validate(options).Should().BeEmpty();
    }

    [Fact]
    public void Validate_CircuitBreakerSamplingWindowBelowOneSecond_ReturnsError()
    {
        var options = new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { CircuitBreakerSamplingWindowSeconds = 0 },
        };

        GatewayOptionsValidation.Validate(options)
            .Should().ContainSingle(e => e.Contains(nameof(GatewayResilienceOptions.CircuitBreakerSamplingWindowSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NegativeShutdownDrain_ReturnsError()
    {
        var options = new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { ShutdownDrainSeconds = -1 },
        };

        GatewayOptionsValidation.Validate(options)
            .Should().ContainSingle(e => e.Contains(nameof(GatewayResilienceOptions.ShutdownDrainSeconds), StringComparison.Ordinal));
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
