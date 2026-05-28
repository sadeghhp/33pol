using FluentAssertions;
using Pol33.Core.Configuration;

namespace Pol33.Core.Tests.Configuration;

public sealed class RateLimitConfigValidationTests
{
    [Fact]
    public void TryValidate_ValidDefaultAndPlans_ReturnsTrue()
    {
        var plans = new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["standard"] = new() { Rpm = 120, Burst = 20, MaxConcurrentStreams = 10 },
        };

        RateLimitConfigValidation.TryValidate(
                new RateLimitTierOptions { Rpm = 60, Burst = 10, MaxConcurrentStreams = 5 },
                plans,
                out var error)
            .Should()
            .BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_RpmBelowMinimum_ReturnsFalse()
    {
        RateLimitConfigValidation.TryValidate(
                new RateLimitTierOptions { Rpm = 0, Burst = 0, MaxConcurrentStreams = 0 },
                new Dictionary<string, RateLimitTierOptions>(),
                out var error)
            .Should()
            .BeFalse();
        error.Should().Contain("default.rpm");
    }

    [Fact]
    public void TryValidate_RpmAboveMaximum_ReturnsFalse()
    {
        RateLimitConfigValidation.TryValidate(
                new RateLimitTierOptions
                {
                    Rpm = RateLimitConfigValidation.MaxRpm + 1,
                    Burst = 0,
                    MaxConcurrentStreams = 0,
                },
                new Dictionary<string, RateLimitTierOptions>(),
                out var error)
            .Should()
            .BeFalse();
        error.Should().Contain("default.rpm");
    }

    [Fact]
    public void TryValidate_NegativeBurst_ReturnsFalse()
    {
        RateLimitConfigValidation.TryValidate(
                new RateLimitTierOptions { Rpm = 1, Burst = -1, MaxConcurrentStreams = 0 },
                new Dictionary<string, RateLimitTierOptions>(),
                out var error)
            .Should()
            .BeFalse();
        error.Should().Contain("default.burst");
    }

    [Fact]
    public void TryValidate_MaxConcurrentStreamsAboveMaximum_ReturnsFalse()
    {
        RateLimitConfigValidation.TryValidate(
                new RateLimitTierOptions
                {
                    Rpm = 1,
                    Burst = 0,
                    MaxConcurrentStreams = RateLimitConfigValidation.MaxMaxConcurrentStreams + 1,
                },
                new Dictionary<string, RateLimitTierOptions>(),
                out var error)
            .Should()
            .BeFalse();
        error.Should().Contain("default.maxConcurrentStreams");
    }

    [Fact]
    public void TryValidate_InvalidPlanSlug_ReturnsFalse()
    {
        var plans = new Dictionary<string, RateLimitTierOptions>
        {
            ["1bad"] = new() { Rpm = 1, Burst = 0, MaxConcurrentStreams = 0 },
        };

        RateLimitConfigValidation.TryValidate(
                new RateLimitTierOptions { Rpm = 1, Burst = 0, MaxConcurrentStreams = 0 },
                plans,
                out var error)
            .Should()
            .BeFalse();
        error.Should().Contain("plan slug");
    }

    [Fact]
    public void TryValidate_PlanTierOutOfRange_ReturnsFalse()
    {
        var plans = new Dictionary<string, RateLimitTierOptions>
        {
            ["enterprise"] = new() { Rpm = 0, Burst = 0, MaxConcurrentStreams = 0 },
        };

        RateLimitConfigValidation.TryValidate(
                new RateLimitTierOptions { Rpm = 1, Burst = 0, MaxConcurrentStreams = 0 },
                plans,
                out var error)
            .Should()
            .BeFalse();
        error.Should().Contain("plans['enterprise'].rpm");
    }

    [Fact]
    public void TryValidate_MissingDefault_ReturnsFalse()
    {
        RateLimitConfigValidation.TryValidate(
                null,
                new Dictionary<string, RateLimitTierOptions>(),
                out var error)
            .Should()
            .BeFalse();
        error.Should().Be("default is required.");
    }
}
