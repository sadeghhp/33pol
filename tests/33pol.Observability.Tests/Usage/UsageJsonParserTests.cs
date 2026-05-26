using Pol33.Core.Identity;
using Pol33.Core.Usage;
using Pol33.Observability.Usage;

namespace Pol33.Observability.Tests.Usage;

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
    public void FromInference_WithTenant_IncludesCostCenter()
    {
        var tenant = new TenantContext
        {
            TenantId = Guid.NewGuid().ToString(),
            ApiKeyId = Guid.NewGuid().ToString(),
            CostCenter = "ops",
            Role = ApiKeyRole.Inference,
        };

        var usage = UsageJsonParser.FromInference(
            "req_1",
            "gpt-4o",
            promptTokens: 10,
            completionTokens: 5,
            durationMs: 33,
            tenant);

        usage.Should().NotBeNull();
        usage!.CostCenter.Should().Be("ops");
    }
}
