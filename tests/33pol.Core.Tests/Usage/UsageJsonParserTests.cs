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

    /// <summary>
    /// Behaviour change, deliberate: total-only usage used to be folded into <c>prompt_tokens</c>,
    /// which priced the whole total at the input rate and under-billed every model that reports only
    /// a total. It is now surfaced as a distinct kind so pricing can apply an explicit policy, and
    /// the split-only overload reports false rather than inventing a split.
    /// </summary>
    [Fact]
    public void TryParseUsage_TotalTokensOnly_IsNotReportedAsASplit()
    {
        var json = """{"usage":{"total_tokens":56}}"""u8.ToArray();

        UsageJsonParser.TryParseUsage(json, out var prompt, out var completion).Should().BeFalse();
        prompt.Should().Be(0);
        completion.Should().Be(0);
    }

    [Fact]
    public void Parse_TotalTokensOnly_ReportsTotalOnlyKind()
    {
        var parsed = UsageJsonParser.Parse("""{"usage":{"total_tokens":56}}"""u8.ToArray());

        parsed.Kind.Should().Be(UsageParseKind.TotalOnly);
        parsed.TotalTokens.Should().Be(56);
        parsed.PromptTokens.Should().Be(0);
        parsed.CompletionTokens.Should().Be(0);
        parsed.HasUsage.Should().BeTrue();
        parsed.BillableTokenTotal.Should().Be(56);
    }

    [Fact]
    public void Parse_SplitUsage_ReportsSplitKind()
    {
        var parsed = UsageJsonParser.Parse("""{"usage":{"prompt_tokens":3,"completion_tokens":7}}"""u8.ToArray());

        parsed.Kind.Should().Be(UsageParseKind.Split);
        parsed.PromptTokens.Should().Be(3);
        parsed.CompletionTokens.Should().Be(7);
        parsed.BillableTokenTotal.Should().Be(10);
    }

    /// <summary>
    /// Every shape below used to throw out of the parser rather than returning a value: the numeric
    /// accessors were called without a ValueKind guard, and the resulting InvalidOperationException /
    /// FormatException was not a JsonException so it escaped the catch — from a Dispose running after
    /// the response body had already been sent to the client.
    /// </summary>
    [Theory]
    [InlineData("""{"usage":{"prompt_tokens":null,"completion_tokens":null}}""")]
    [InlineData("""{"usage":{"prompt_tokens":"12","completion_tokens":"3"}}""")]
    [InlineData("""{"usage":{"prompt_tokens":1.5,"completion_tokens":2.5}}""")]
    [InlineData("""{"usage":{"prompt_tokens":-5,"completion_tokens":-2}}""")]
    [InlineData("""{"usage":{"prompt_tokens":99999999999999999999999,"completion_tokens":1e40}}""")]
    [InlineData("""{"usage":{"prompt_tokens":{},"completion_tokens":[]}}""")]
    [InlineData("""{"usage":"not-an-object"}""")]
    [InlineData("""{"usage":null}""")]
    [InlineData("""{"usage":[]}""")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{not-json")]
    public void Parse_MalformedUsage_ReturnsNoneWithoutThrowing(string body)
    {
        var act = () => UsageJsonParser.Parse(System.Text.Encoding.UTF8.GetBytes(body));

        act.Should().NotThrow();
        act().Kind.Should().Be(UsageParseKind.None);
        act().HasUsage.Should().BeFalse();
    }

    /// <summary>
    /// A partly-usable object still yields what it legitimately can: one valid side plus one
    /// unusable side is a split with the unusable side at zero, not a total-only fallback.
    /// </summary>
    [Fact]
    public void Parse_OneUsableSide_ReportsSplitWithZeroForTheOther()
    {
        var parsed = UsageJsonParser.Parse("""{"usage":{"prompt_tokens":8,"completion_tokens":null}}"""u8.ToArray());

        parsed.Kind.Should().Be(UsageParseKind.Split);
        parsed.PromptTokens.Should().Be(8);
        parsed.CompletionTokens.Should().Be(0);
    }

    /// <summary>Zero-token usage carries no billable signal and must not be mistaken for a real split.</summary>
    [Fact]
    public void Parse_AllZeroCounts_ReturnsNone()
    {
        UsageJsonParser.Parse("""{"usage":{"prompt_tokens":0,"completion_tokens":0}}"""u8.ToArray())
            .Kind.Should().Be(UsageParseKind.None);
    }

    /// <summary>A malformed split must not silently downgrade to the total, which prices differently.</summary>
    [Fact]
    public void Parse_UnusableSplitWithValidTotal_FallsBackToTotalOnly()
    {
        var parsed = UsageJsonParser.Parse(
            """{"usage":{"prompt_tokens":null,"completion_tokens":"x","total_tokens":42}}"""u8.ToArray());

        parsed.Kind.Should().Be(UsageParseKind.TotalOnly);
        parsed.TotalTokens.Should().Be(42);
    }

    /// <summary>
    /// When exactly one side and the total are reported, the other side is known exactly. Billing it
    /// as zero under-charged some OpenAI-compatible servers that omit a zero-valued field or report
    /// only prompt/total.
    /// </summary>
    [Fact]
    public void TryParseUsage_PromptAndTotalTokens_DerivesCompletionFromTotal()
    {
        var json = """{"usage":{"prompt_tokens":3,"total_tokens":99}}"""u8.ToArray();
        UsageJsonParser.TryParseUsage(json, out var prompt, out var completion).Should().BeTrue();
        prompt.Should().Be(3);
        completion.Should().Be(96);
    }

    [Fact]
    public void Parse_CompletionAndTotalTokens_DerivesPromptFromTotal()
    {
        var parsed = UsageJsonParser.Parse("""{"usage":{"completion_tokens":10,"total_tokens":25}}"""u8.ToArray());

        parsed.Kind.Should().Be(UsageParseKind.Split);
        parsed.PromptTokens.Should().Be(15);
        parsed.CompletionTokens.Should().Be(10);
    }

    /// <summary>An explicit split always wins; a disagreeing total does not rewrite either side.</summary>
    [Fact]
    public void Parse_BothSidesAndTotal_IgnoresTheTotal()
    {
        var parsed = UsageJsonParser.Parse("""{"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":99}}"""u8.ToArray());

        parsed.PromptTokens.Should().Be(3);
        parsed.CompletionTokens.Should().Be(4);
    }

    /// <summary>A total smaller than the reported side is inconsistent; nothing is derived from it.</summary>
    [Fact]
    public void Parse_OneSideWithSmallerTotal_DoesNotDeriveANegativeOtherSide()
    {
        var parsed = UsageJsonParser.Parse("""{"usage":{"prompt_tokens":30,"total_tokens":20}}"""u8.ToArray());

        parsed.Kind.Should().Be(UsageParseKind.Split);
        parsed.PromptTokens.Should().Be(30);
        parsed.CompletionTokens.Should().Be(0);
    }

    [Fact]
    public void Parse_OneSideZeroWithTotal_DerivesTheOtherSide()
    {
        var parsed = UsageJsonParser.Parse("""{"usage":{"prompt_tokens":0,"total_tokens":7}}"""u8.ToArray());

        parsed.Kind.Should().Be(UsageParseKind.Split);
        parsed.PromptTokens.Should().Be(0);
        parsed.CompletionTokens.Should().Be(7);
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
    public void ParseSseText_MalformedFrames_AreSkippedWithoutThrowing()
    {
        const string sse = """
            data: {"usage":{"prompt_tokens":"bad"}}

            data: {"usage":{"prompt_tokens":9,"completion_tokens":2}}

            data: {not-json

            data: [DONE]

            """;

        var parsed = UsageJsonParser.ParseSseText(sse);

        parsed.Kind.Should().Be(UsageParseKind.Split);
        parsed.PromptTokens.Should().Be(9);
        parsed.CompletionTokens.Should().Be(2);
    }

    /// <summary>
    /// The tail buffer starts mid-stream, so its first line is usually a truncated frame. It must be
    /// skipped rather than derailing the scan.
    /// </summary>
    [Fact]
    public void ParseSseText_LeadingPartialFrame_IsIgnored()
    {
        const string sse = """
            ompletion_tokens":3}}

            data: {"usage":{"prompt_tokens":5,"completion_tokens":6}}

            data: [DONE]

            """;

        var parsed = UsageJsonParser.ParseSseText(sse);

        parsed.PromptTokens.Should().Be(5);
        parsed.CompletionTokens.Should().Be(6);
    }

    [Fact]
    public void ParseSseText_TotalOnlyFrame_ReportsTotalOnly()
    {
        const string sse = """
            data: {"usage":{"total_tokens":31}}

            data: [DONE]

            """;

        var parsed = UsageJsonParser.ParseSseText(sse);

        parsed.Kind.Should().Be(UsageParseKind.TotalOnly);
        parsed.TotalTokens.Should().Be(31);
    }

    [Fact]
    public void ParseSseText_NoUsageFrame_ReturnsNone()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"hi"}}]}

            data: [DONE]

            """;

        UsageJsonParser.ParseSseText(sse).Kind.Should().Be(UsageParseKind.None);
    }

    [Fact]
    public void FromParsedUsage_TotalOnly_CarriesTheKindAndTotal()
    {
        var usage = UsageEventFactory.FromParsedUsage(
            "req_1",
            "gpt-4o",
            ParsedUsage.TotalOnly(40),
            durationMs: 10);

        usage.TokenSource.Should().Be(Pol33.Core.Models.UsageTokenSource.TotalOnly);
        usage.TotalTokens.Should().Be(40);
        usage.PromptTokens.Should().Be(0);
        usage.CompletionTokens.Should().Be(0);
    }

    [Fact]
    public void FromParsedUsage_Split_CarriesTheSplitAndNoTotal()
    {
        var usage = UsageEventFactory.FromParsedUsage(
            "req_1",
            "gpt-4o",
            ParsedUsage.Split(4, 6),
            durationMs: 10);

        usage.TokenSource.Should().Be(Pol33.Core.Models.UsageTokenSource.Split);
        usage.PromptTokens.Should().Be(4);
        usage.CompletionTokens.Should().Be(6);
        usage.TotalTokens.Should().Be(0);
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
