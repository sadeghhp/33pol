using Pol33.Core.Identity;
using Pol33.Core.Usage;

namespace Pol33.Core.Tests.Usage;

public sealed class UsageJsonParserTests
{
    [Fact]
    public void TryParseUsage_ValidPayload_ReturnsTokenCounts()
    {
        var json = """{"usage":{"prompt_tokens":3,"completion_tokens":7}}"""u8.ToArray();
        UsageJsonParser.TryParseUsage(json, out var prompt, out var completion).Should().BeTrue();
        prompt.Should().Be(3);
        completion.Should().Be(7);
    }

    [Fact]
    public void TryParseUsage_MissingUsage_ReturnsFalse()
    {
        UsageJsonParser.TryParseUsage("""{"id":"x"}"""u8.ToArray(), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseUsage_InvalidJson_ReturnsFalse()
    {
        UsageJsonParser.TryParseUsage("{not-json"u8.ToArray(), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseUsage_TotalTokensOnly_MapsToPromptTokens()
    {
        var json = """{"usage":{"total_tokens":56}}"""u8.ToArray();
        UsageJsonParser.TryParseUsage(json, out var prompt, out var completion).Should().BeTrue();
        prompt.Should().Be(56);
        completion.Should().Be(0);
    }

    [Fact]
    public void TryParseUsage_PromptAndTotalTokens_PrefersPromptTokens()
    {
        var json = """{"usage":{"prompt_tokens":3,"total_tokens":99}}"""u8.ToArray();
        UsageJsonParser.TryParseUsage(json, out var prompt, out var completion).Should().BeTrue();
        prompt.Should().Be(3);
        completion.Should().Be(0);
    }

    [Fact]
    public void TryParseUsageFromSseText_LastDataLineWithUsage_ReturnsCounts()
    {
        const string sse = """
            data: {"choices":[]}

            data: {"usage":{"prompt_tokens":11,"completion_tokens":4}}

            data: [DONE]

            """;

        UsageJsonParser.TryParseUsageFromSseText(sse, out var prompt, out var completion).Should().BeTrue();
        prompt.Should().Be(11);
        completion.Should().Be(4);
    }

    [Fact]
    public void FromInference_WithTenant_IncludesCostCenter()
    {
        var tenant = new TenantContext
        {
            TenantId = Guid.NewGuid().ToString(),
            ApiKeyId = Guid.NewGuid().ToString(),
            CostCenter = "ops",
            Role = ApiKeyRole.Inference,
        };

        var usage = UsageEventFactory.FromInference(
            "req_1",
            "gpt-4o",
            promptTokens: 10,
            completionTokens: 5,
            durationMs: 33,
            tenant);

        usage.CostCenter.Should().Be("ops");
    }
}
