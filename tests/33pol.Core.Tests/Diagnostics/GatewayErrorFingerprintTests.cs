using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// The regression that brought this file here: Compute_IgnoresCompilerGeneratedFrameNames failed
    /// with RegexMatchTimeoutException on inputs that match in microseconds. Every pattern on this
    /// path is linear and every input is capped upstream, so the 100ms budget protected nothing —
    /// but it is wall-clock, so any thread stall longer than the budget threw regardless of how
    /// little the match had done. Under a full test run on a loaded box that fired often enough to
    /// break the suite, and in production it would have turned recording a fault into a fault.
    /// </summary>
    [Fact]
    public void FingerprintRegexes_DeclareNoWallClockMatchTimeout()
    {
        var factories = typeof(GatewayErrorFingerprint)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Regex) && m.GetParameters().Length == 0)
            .ToArray();

        factories.Should().NotBeEmpty("the fingerprint still normalizes with source-generated regexes");

        foreach (var factory in factories)
        {
            var regex = (Regex)factory.Invoke(null, null)!;
            regex.MatchTimeout.Should().Be(
                Regex.InfiniteMatchTimeout,
                "{0} runs on the error path, where a wall-clock timeout only invents failures",
                factory.Name);
        }
    }

    [Theory]
    // A plain owned frame keeps namespace, type and member, and drops the argument list and the
    // file/line tail.
    [InlineData(
        "   at Pol33.Proxy.Middleware.ModelRouterMiddleware.InvokeAsync(HttpContext c) in /src/F.cs:line 42",
        "Pol33.Proxy.Middleware.ModelRouterMiddleware.InvokeAsync")]
    // Async state machine and lambda display class both collapse onto the same owned member.
    [InlineData(
        "   at Pol33.Proxy.Middleware.ModelRouterMiddleware.<InvokeAsync>d__12.MoveNext()",
        "Pol33.Proxy.Middleware.ModelRouterMiddleware.InvokeAsync")]
    [InlineData(
        "   at Pol33.Proxy.Middleware.ModelRouterMiddleware.<>c__DisplayClass3_0.<InvokeAsync>b__0()",
        "Pol33.Proxy.Middleware.ModelRouterMiddleware.InvokeAsync")]
    // Generic arity survives; the name stops at the nested-type separator.
    [InlineData("   at Pol33.Core.Foo`1+Nested[[System.Int32]].Bar(x)", "Pol33.Core.Foo`1")]
    // Tabs count as the separator, and "at" need not stand alone.
    [InlineData("\tat Pol33.Core.A.B()", "Pol33.Core.A.B")]
    // Frames that are not ours, and lines that are not frames at all, contribute nothing.
    [InlineData("   at System.Threading.Tasks.Task`1.get_Result()", null)]
    [InlineData("System.InvalidOperationException: boom", null)]
    [InlineData("--- End of stack trace from previous location ---", null)]
    // Malformed and truncated frame lines must not be mistaken for frames.
    [InlineData("at", null)]
    [InlineData("at ", null)]
    [InlineData("at Pol33.", null)]
    [InlineData("at Pol33.Orphan", null)]
    [InlineData("atPol33.A.B", null)]
    [InlineData("at Pol33x.A.B", null)]
    [InlineData("at pol33.A.B", null)]
    public void Compute_ExtractsTheOwnedFrameFromOneLine(string stackTrace, string? expectedFrame)
    {
        // The frame is hashed rather than exposed, so assert through a record whose stack trace is
        // already reduced to the expected frame: equal fingerprints mean equal extracted frames.
        var reference = Record() with
        {
            StackTrace = expectedFrame is null ? null : $"   at {expectedFrame}",
        };

        GatewayErrorFingerprint.Compute(Record() with { StackTrace = stackTrace })
            .Should().Be(
                GatewayErrorFingerprint.Compute(reference),
                "'{0}' should reduce to the owned frame '{1}'", stackTrace, expectedFrame ?? "none");
    }

    /// <summary>
    /// The first owned frame wins, even when framework frames sit above it.
    /// </summary>
    [Fact]
    public void Compute_UsesTheFirstOwnedFrameBelowFrameworkFrames()
    {
        var trace = string.Join('\n',
            "System.InvalidOperationException: boom",
            "   at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()",
            "   at System.Threading.Tasks.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task t)",
            "   at Pol33.Proxy.Middleware.ModelRouterMiddleware.InvokeAsync(HttpContext c)",
            "   at Pol33.Proxy.Middleware.GatewayExceptionHandlingMiddleware.InvokeAsync(HttpContext c)");

        var direct = Record() with
        {
            StackTrace = "   at Pol33.Proxy.Middleware.ModelRouterMiddleware.InvokeAsync(HttpContext c)",
        };

        GatewayErrorFingerprint.Compute(Record() with { StackTrace = trace })
            .Should().Be(GatewayErrorFingerprint.Compute(direct));
    }

    /// <summary>
    /// Local functions carry a <c>|3_0</c> suffix that shifts like any other compiler-generated
    /// number. It falls outside the frame name, so the group key is already stable across it.
    /// </summary>
    [Fact]
    public void Compute_IgnoresLocalFunctionOrdinals()
    {
        var before = Record() with { StackTrace = "   at Pol33.Proxy.Router.<Route>g__Resolve|3_0()" };
        var after = Record() with { StackTrace = "   at Pol33.Proxy.Router.<Route>g__Resolve|7_0()" };

        GatewayErrorFingerprint.Compute(before).Should().Be(GatewayErrorFingerprint.Compute(after));
    }

    /// <summary>
    /// Error-path work stays bounded. The budget here is enormous compared with the ~0.04ms the
    /// parse actually costs; it is sized to catch runaway backtracking, not to measure speed.
    /// </summary>
    [Fact]
    public void Compute_StaysBoundedOnAdversarialStackTraces()
    {
        var adversarial = new[]
        {
            "   at Pol33." + new string('a', 8000),
            "   at Pol33." + string.Join('.', Enumerable.Repeat("Seg", 2000)),
            string.Concat(Enumerable.Repeat("at Pol33.", 900)) + new string('a', 100),
            "   at" + new string(' ', 8000) + "Pol33x",
            string.Join('\n', Enumerable.Repeat("   at System.Threading.Tasks.Task`1.get_Result()", 200)),
            new string('<', 4000) + new string('>', 4000),
        };

        foreach (var stackTrace in adversarial)
        {
            var record = Record(message: new string('x', 1000)) with { StackTrace = stackTrace };

            var stopwatch = Stopwatch.StartNew();
            var act = () => GatewayErrorFingerprint.Compute(record);

            act.Should().NotThrow();
            stopwatch.Stop();
            stopwatch.Elapsed.Should().BeLessThan(
                TimeSpan.FromSeconds(5),
                "fingerprinting an 8KB stack trace is a linear scan");
        }
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
