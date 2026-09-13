using System.Text.Json;
using FluentAssertions;
using Pol33.Api.Contracts;
using Pol33.Core.Configuration;

namespace Pol33.Api.Tests.Contracts;

public sealed class AdminRateLimitsDtoTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RoundTrip_UsesCamelCasePropertyNames()
    {
        var dto = new AdminRateLimitsDto
        {
            Default = new RateLimitTierOptions { Rpm = 60, Burst = 10, MaxConcurrentStreams = 5 },
            Plans = new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["standard"] = new() { Rpm = 120, Burst = 20, MaxConcurrentStreams = 10 },
            },
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        json.Should().Contain("\"default\"");
        json.Should().Contain("\"rpm\"");
        json.Should().Contain("\"plans\"");

        var roundTrip = JsonSerializer.Deserialize<AdminRateLimitsDto>(json, JsonOptions);
        roundTrip.Should().NotBeNull();
        roundTrip!.Default.Rpm.Should().Be(60);
        roundTrip.Plans["standard"].Rpm.Should().Be(120);
    }

    /// <summary>The singleton scopes travel as ordinary rules with target <c>*</c>.</summary>
    [Fact]
    public void RoundTrip_CarriesSingletonRules()
    {
        var dto = new AdminRateLimitsDto
        {
            Rules =
            [
                new AdminRateLimitRuleDto("auth_failure", "*", 60, 20, 0),
                new AdminRateLimitRuleDto("anonymous", "*", 60, 20, 2),
            ],
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        json.Should().Contain("\"anonymous\"");

        var roundTrip = JsonSerializer.Deserialize<AdminRateLimitsDto>(json, JsonOptions);
        roundTrip!.Rules.Should().HaveCount(2);
        roundTrip.Rules![1].ToDefinition().Scope.Should().Be("anonymous");
        roundTrip.Rules[1].ToDefinition().TargetKey.Should().Be("*");
        roundTrip.Rules[1].MaxConcurrentStreams.Should().Be(2);
    }
}
