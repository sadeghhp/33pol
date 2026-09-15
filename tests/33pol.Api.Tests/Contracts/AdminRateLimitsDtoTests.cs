using System.Text.Json;
using FluentAssertions;
using Pol33.Api.Contracts;
using Pol33.Core.Configuration;

namespace Pol33.Api.Tests.Contracts;

public sealed class AdminRateLimitsDtoTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The scope is stored verbatim and compared against the canonical constants everywhere else, so
    /// it is canonicalised on the way in rather than merely trimmed. Without it a payload spelled
    /// <c>"Anonymous"</c> was persisted as written and never matched the singleton it names.
    /// </summary>
    [Theory]
    [InlineData("Anonymous", "anonymous")]
    [InlineData("  AUTH_FAILURE  ", "auth_failure")]
    [InlineData("Tenant_Model", "tenant_model")]
    [InlineData("model", "model")]
    public void ToDefinition_CanonicalisesTheScope(string submitted, string expected)
    {
        var dto = new AdminRateLimitRuleDto(submitted, "*", 60, 0, 0);

        dto.ToDefinition().Scope.Should().Be(expected);
    }

    /// <summary>An unrecognised scope is left alone so the validator can name it in its message.</summary>
    [Fact]
    public void ToDefinition_AnUnknownScope_IsLeftAsWritten()
    {
        var dto = new AdminRateLimitRuleDto(" Region ", "eu-west", 60, 0, 0);

        dto.ToDefinition().Scope.Should().Be("Region");
    }

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

    /// <summary>A rule's windows ride along as a camel-cased array; a missing array stays null (keep stored).</summary>
    [Fact]
    public void RoundTrip_CarriesScheduleWindows_AndDistinguishesAbsentFromEmpty()
    {
        var dto = new AdminRateLimitsDto
        {
            Rules =
            [
                new AdminRateLimitRuleDto("model", "gpt-4", 600, 60, 40)
                {
                    Schedule =
                    [
                        new AdminRateLimitWindowDto(
                            "off-peak", "weekly", 1200, 200, 80,
                            Days: ["mon", "fri"], Start: "19:00", End: "07:00", TimeZone: "Europe/Berlin"),
                        new AdminRateLimitWindowDto(
                            "launch", "once", 3000, 500, 120,
                            From: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
                    ],
                },
                new AdminRateLimitRuleDto("model", "llama-70b", 0, 0, 8),
            ],
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        json.Should().Contain("\"schedule\"").And.Contain("\"timeZone\"").And.Contain("\"days\"");

        var roundTrip = JsonSerializer.Deserialize<AdminRateLimitsDto>(json, JsonOptions)!;
        var scheduled = roundTrip.Rules![0].ToDefinition();
        scheduled.Schedule.Should().HaveCount(2);
        scheduled.Schedule![0].IsWeekly.Should().BeTrue();
        scheduled.Schedule[0].Days.Should().Equal("mon", "fri");
        scheduled.Schedule[1].From.Should().Be(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        scheduled.Schedule[1].Until.Should().BeNull();

        roundTrip.Rules[1].ToDefinition().Schedule.Should().BeNull("an absent schedule means keep what is stored");
    }

    /// <summary>
    /// The enabled flag crosses the wire, and an absent one means enforced — the important half.
    /// Were the default false, a client that predates the field would appear to switch off every rule
    /// it round-tripped.
    /// </summary>
    [Fact]
    public void RoundTrip_CarriesEnabled_AndTreatsAnAbsentFlagAsEnforced()
    {
        var dto = new AdminRateLimitsDto
        {
            Rules =
            [
                new AdminRateLimitRuleDto("model", "switched-off", 600, 60, 40) { Enabled = false },
                new AdminRateLimitRuleDto("model", "still-on", 600, 60, 40),
            ],
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        json.Should().Contain("\"enabled\"");

        var roundTrip = JsonSerializer.Deserialize<AdminRateLimitsDto>(json, JsonOptions)!;
        roundTrip.Rules![0].ToDefinition().Enabled.Should().BeFalse();
        roundTrip.Rules[1].ToDefinition().Enabled.Should().BeTrue();

        // A payload from a client that has never heard of the field.
        var legacy = JsonSerializer.Deserialize<AdminRateLimitsDto>(
            """
            { "rules": [ { "scope": "model", "target": "gpt-4", "rpm": 600, "burst": 60, "maxConcurrentStreams": 0 } ] }
            """,
            JsonOptions)!;
        legacy.Rules![0].Enabled.Should().BeTrue();
        legacy.Rules[0].ToDefinition().Enabled.Should().BeTrue();
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
