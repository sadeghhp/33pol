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
}
