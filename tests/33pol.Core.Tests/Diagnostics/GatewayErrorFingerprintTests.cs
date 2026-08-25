using Pol33.Core.Diagnostics;
using Pol33.Core.Models;

namespace Pol33.Core.Tests.Diagnostics;

public sealed class GatewayErrorFingerprintTests
{
    [Fact]
    public void Compute_IsStableForTheSameFailure()
    {
        var first = Record();
        var second = Record();

        GatewayErrorFingerprint.Compute(first).Should().Be(GatewayErrorFingerprint.Compute(second));
    }

    [Fact]
    public void Compute_IgnoresPerOccurrenceNoiseInTheMessage()
    {
        // The same fault, reported with a different request id, GUID and elapsed time. Grouping on
        // the raw message would give each occurrence its own row, which is the flat list again.
        var a = Record(message: "Upstream returned 502 for request req_0123456789abcdef0123456789abcdef after 1204ms");
        var b = Record(message: "Upstream returned 502 for request req_fedcba9876543210fedcba9876543210 after 87ms");

        GatewayErrorFingerprint.Compute(a).Should().Be(GatewayErrorFingerprint.Compute(b));
    }

    [Fact]
    public void Compute_IgnoresTheUpstreamHost()
    {
        // One failing replica out of several must not look like a distinct fault.
        var a = Record() with { UpstreamTarget = "http://replica-1:8000" };
        var b = Record() with { UpstreamTarget = "http://replica-2:8000" };

        GatewayErrorFingerprint.Compute(a).Should().Be(GatewayErrorFingerprint.Compute(b));
    }

    [Fact]
    public void Compute_IgnoresRequestAndTenantIdentity()
    {
        var a = Record() with { RequestId = "req_a", TenantId = "tenant-a", DurationMs = 12 };
        var b = Record() with { RequestId = "req_b", TenantId = "tenant-b", DurationMs = 9000 };

        GatewayErrorFingerprint.Compute(a).Should().Be(GatewayErrorFingerprint.Compute(b));
    }

    [Theory]
    [InlineData("ModelId", "other-model")]
    [InlineData("StatusCode", "503")]
    [InlineData("ExceptionType", "System.TimeoutException")]
    [InlineData("Level", "Warning")]
    [InlineData("Source", "log")]
    [InlineData("RouteKind", "embeddings")]
    [InlineData("EventCode", "upstream_timeout")]
    public void Compute_SeparatesGenuinelyDifferentFailures(string field, string value)
    {
        var baseline = Record();
        var changed = field switch
        {
            "ModelId" => baseline with { ModelId = value },
            "StatusCode" => baseline with { StatusCode = int.Parse(value) },
            "ExceptionType" => baseline with { ExceptionType = value },
            "Level" => baseline with { Level = value },
            "Source" => baseline with { Source = value },
            "RouteKind" => baseline with { RouteKind = value },
            _ => baseline with { EventCode = value },
        };

        GatewayErrorFingerprint.Compute(changed).Should().NotBe(GatewayErrorFingerprint.Compute(baseline));
    }

    [Fact]
    public void Compute_SeparatesDifferentMessages()
    {
        var a = Record(message: "Upstream returned 502 for model 'gpt-4o'.");
        var b = Record(message: "Rejected: circuit breaker open for model 'gpt-4o'.");

        GatewayErrorFingerprint.Compute(a).Should().NotBe(GatewayErrorFingerprint.Compute(b));
    }

    [Fact]
    public void Compute_IgnoresStackTraceLineNumbers()
    {
        // Line numbers move with every edit; including them would silently re-fingerprint every
        // existing fault on release.
        var a = Record() with
        {
            StackTrace = "System.Exception: boom\n   at Pol33.Proxy.Middleware.ModelRouterMiddleware.InvokeAsync() in /src/File.cs:line 42",
        };
        var b = Record() with
        {
            StackTrace = "System.Exception: boom\n   at Pol33.Proxy.Middleware.ModelRouterMiddleware.InvokeAsync() in /src/File.cs:line 88",
        };

        GatewayErrorFingerprint.Compute(a).Should().Be(GatewayErrorFingerprint.Compute(b));
    }

    [Fact]
    public void Compute_ReturnsSixteenLowercaseHexCharacters()
    {
        GatewayErrorFingerprint.Compute(Record()).Should().MatchRegex("^[0-9a-f]{16}$");
    }

    /// <summary>Digits that name something are identity; digits that count something are noise.</summary>
    [Fact]
    public void NormalizeMessage_KeepsDigitsInsideNamesButNotFreeStandingNumbers()
    {
        GatewayErrorFingerprint.NormalizeMessage("Model gpt-4o not found")
            .Should().NotBe(GatewayErrorFingerprint.NormalizeMessage("Model gpt-5 not found"));
        GatewayErrorFingerprint.NormalizeMessage("Qwen3-VL-8B timed out after 30s")
            .Should().Be(GatewayErrorFingerprint.NormalizeMessage("Qwen3-VL-8B timed out after 300s"));
        GatewayErrorFingerprint.NormalizeMessage("Upstream returned HTTP 401")
            .Should().Be(GatewayErrorFingerprint.NormalizeMessage("Upstream returned HTTP 403"));
    }

    /// <summary>
    /// Compiler-generated names carry numbers that shift whenever a lambda or await is added
    /// anywhere in the type; they must not re-key every existing group on release.
    /// </summary>
    [Fact]
    public void Compute_IgnoresCompilerGeneratedFrameNames()
    {
        var before = Record() with
        {
            StackTrace = "   at Pol33.Proxy.Middleware.ModelRouterMiddleware.<InvokeAsync>d__12.MoveNext()",
        };
        var after = Record() with
        {
            StackTrace = "   at Pol33.Proxy.Middleware.ModelRouterMiddleware.<InvokeAsync>d__14.MoveNext()",
        };
        var lambda = Record() with
        {
            StackTrace = "   at Pol33.Proxy.Middleware.ModelRouterMiddleware.<>c__DisplayClass3_0.<InvokeAsync>b__0()",
        };

        GatewayErrorFingerprint.Compute(before).Should().Be(GatewayErrorFingerprint.Compute(after));
        GatewayErrorFingerprint.Compute(before).Should().Be(GatewayErrorFingerprint.Compute(lambda));
    }

    [Fact]
    public void NormalizeMessage_HandlesBlankInput()
    {
        GatewayErrorFingerprint.NormalizeMessage(null).Should().Be("none");
        GatewayErrorFingerprint.NormalizeMessage("   ").Should().Be("none");
    }

    private static GatewayErrorRecord Record(string message = "Upstream returned 502 for model 'gpt-4o'.") => new()
    {
        Id = "err_1",
        Fingerprint = string.Empty,
        OccurredAt = DateTimeOffset.UnixEpoch,
        Level = "Error",
        Source = GatewayErrorSourceNames.Proxy,
        Category = "ModelRouterMiddleware",
        EventCode = "upstream_error",
        Message = message,
        StatusCode = 502,
        ModelId = "gpt-4o",
        RouteKind = "chat",
    };
}
