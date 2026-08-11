using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.Forwarding;

public sealed class StreamingHttpTransformerTests
{
    [Fact]
    public async Task TransformRequestAsync_SetsOutboundUriForOpenRouterBase()
    {
        var transformer = new StreamingHttpTransformer(
            isStreaming: false,
            clientModelName: null,
            canonicalModelId: "gpt-4o");
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        var proxyRequest = new HttpRequestMessage(HttpMethod.Post, "http://upstream/v1/chat/completions");

        await transformer.TransformRequestAsync(
            context,
            proxyRequest,
            "https://openrouter.ai/api",
            CancellationToken.None);

        proxyRequest.RequestUri!.AbsoluteUri.Should().Be("https://openrouter.ai/api/v1/chat/completions");
    }

    /// <summary>
    /// The alias rewrite splices the model token out of the buffered body, so everything else —
    /// spacing, key order, nested occurrences of "model", non-ASCII content — must survive verbatim.
    /// </summary>
    [Theory]
    [InlineData("""{"model":"alias","stream":true}""", """{"model":"canonical","stream":true}""")]
    [InlineData("""{"model" :   "alias"  ,"n":1}""", """{"model" :   "canonical"  ,"n":1}""")]
    [InlineData("""{"messages":[{"model":"nested"}],"model":"alias"}""", """{"messages":[{"model":"nested"}],"model":"canonical"}""")]
    [InlineData("""{"model":"alias","t":"héllo ✓"}""", """{"model":"canonical","t":"héllo ✓"}""")]
    public async Task TransformRequestAsync_Alias_SplicesCanonicalIdAndPreservesEverythingElse(
        string body,
        string expected)
    {
        var (context, proxyRequest) = await RunAliasTransformAsync(body);

        var forwarded = await proxyRequest.Content!.ReadAsStringAsync();
        forwarded.Should().Be(expected);
        proxyRequest.Content.Headers.ContentLength
            .Should().Be(Encoding.UTF8.GetByteCount(expected), "the spliced length must be declared exactly");

        // The buffered body itself is untouched: usage capture reads its length afterwards.
        context.Request.Body.Length.Should().Be(Encoding.UTF8.GetByteCount(body));
    }

    [Fact]
    public async Task TransformRequestAsync_ModelMatchesCanonicalId_ForwardsBodyUnchanged()
    {
        var (_, proxyRequest) = await RunAliasTransformAsync(
            """{"model":"canonical"}""",
            clientModelName: "canonical");

        // No replacement content was installed, so the forwarder's own StreamContent still stands.
        proxyRequest.Content.Should().BeOfType<StreamContent>();
    }

    /// <summary>
    /// A transformer built without a model range recovers it by re-scanning rather than falling back
    /// to materialising the body.
    /// </summary>
    [Fact]
    public async Task TransformRequestAsync_AliasWithoutSuppliedRange_StillSplices()
    {
        var (_, proxyRequest) = await RunAliasTransformAsync(
            """{"model":"alias","stream":false}""",
            supplyRange: false);

        (await proxyRequest.Content!.ReadAsStringAsync())
            .Should().Be("""{"model":"canonical","stream":false}""");
    }

    /// <summary>
    /// The defect this covers: the rewrite used to read the body into a string, re-parse it into a
    /// JsonDocument, write it back through a growing MemoryStream, copy that with ToArray, decode it
    /// and re-encode it — ~13x the body size in Large Object Heap allocations per aliased request. At
    /// the default 25 MB body cap that alone exceeded what a gateway pod is given.
    /// </summary>
    [Fact]
    public async Task TransformRequestAsync_LargeAliasedBody_SplicesWithoutCopyingTheBody()
    {
        const int payloadBytes = 8 * 1024 * 1024;
        var body = $$"""{"model":"alias","messages":[{"role":"user","content":"{{new string('x', payloadBytes)}}"}]}""";
        var expectedLength = body.Length + "canonical".Length - "alias".Length;

        // Everything the test itself allocates — the body, its bytes, the parse — happens before the
        // window, so only the transform is measured. Thread-local so a parallel run cannot perturb
        // it; every stream in play is a MemoryStream, so the awaits stay on this thread.
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var parsed = await Pol33.Proxy.Parsing.InferenceRequestParser
            .ParseAsync(context.Request.Body, CancellationToken.None);
        context.Request.Body.Position = 0;

        var transformer = new StreamingHttpTransformer(
            isStreaming: false,
            clientModelName: "alias",
            canonicalModelId: "canonical",
            modelValueRange: parsed.ModelValueRange);
        var proxyRequest = new HttpRequestMessage(HttpMethod.Post, "http://upstream/v1/chat/completions")
        {
            Content = new StreamContent(context.Request.Body),
        };

        var before = GC.GetAllocatedBytesForCurrentThread();
        await transformer.TransformRequestAsync(context, proxyRequest, "http://upstream", CancellationToken.None);
        var content = proxyRequest.Content!;
        var declaredLength = content.Headers.ContentLength;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        declaredLength.Should().Be(expectedLength);
        allocated.Should().BeLessThan(
            payloadBytes / 8,
            "the splice must stream from the buffered body rather than copy it");

        // Correctness at size, not just cheapness.
        var forwarded = await content.ReadAsStringAsync();
        forwarded.Should().StartWith("""{"model":"canonical","messages":""");
        forwarded.Length.Should().Be(expectedLength);
    }

    private static async Task<(DefaultHttpContext Context, HttpRequestMessage ProxyRequest)> RunAliasTransformAsync(
        string body,
        string clientModelName = "alias",
        bool supplyRange = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var parsed = await Pol33.Proxy.Parsing.InferenceRequestParser
            .ParseAsync(context.Request.Body, CancellationToken.None);
        context.Request.Body.Position = 0;

        var transformer = new StreamingHttpTransformer(
            isStreaming: false,
            clientModelName: clientModelName,
            canonicalModelId: "canonical",
            modelValueRange: supplyRange ? parsed.ModelValueRange : null);

        var proxyRequest = new HttpRequestMessage(HttpMethod.Post, "http://upstream/v1/chat/completions")
        {
            // Mirrors InferenceHttpForwarder, which installs the pass-through content before the
            // transformer runs.
            Content = new StreamContent(context.Request.Body),
        };

        await transformer.TransformRequestAsync(
            context,
            proxyRequest,
            "http://upstream",
            CancellationToken.None);

        return (context, proxyRequest);
    }

    [Fact]
    public async Task TransformResponseAsync_NonStreaming_CapturesUsageAndPreservesResponseBody()
    {
        var usageRecorder = Substitute.For<IUsageRecorder>();
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var usageCapture = new InferenceUsageCapture(
            usageRecorder,
            metrics,
            canonicalModelId: "mock-gpt",
            requestId: "req-1",
            startedUtc: DateTimeOffset.UtcNow,
            tenant: null);

        var transformer = new StreamingHttpTransformer(
            isStreaming: false,
            clientModelName: null,
            canonicalModelId: "mock-gpt",
            usageCapture: usageCapture);

        var payload = """{"usage":{"prompt_tokens":3,"completion_tokens":2}}""";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };

        var shouldCopyBody = await transformer.TransformResponseAsync(
            new DefaultHttpContext(),
            response,
            CancellationToken.None);

        shouldCopyBody.Should().BeTrue();
        response.Content.Should().BeOfType<System.Net.Http.StreamContent>();
        var copiedBody = await response.Content.ReadAsStringAsync();
        copiedBody.Should().Be(payload);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        usageRecorder.Received(1).Enqueue(Arg.Any<Pol33.Core.Models.UsageEvent>());
    }

    [Fact]
    public async Task TransformResponseAsync_Streaming_LargeBodyWithTrailingUsage_CapturesUsage()
    {
        var usageRecorder = Substitute.For<IUsageRecorder>();
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var usageCapture = new InferenceUsageCapture(
            usageRecorder,
            metrics,
            canonicalModelId: "mock-gpt",
            requestId: "req-1",
            startedUtc: DateTimeOffset.UtcNow,
            tenant: null);

        var transformer = new StreamingHttpTransformer(
            isStreaming: true,
            clientModelName: null,
            canonicalModelId: "mock-gpt",
            usageCapture: usageCapture);

        // SSE body larger than the 512 KB capture buffer, with the usage chunk at the very end.
        var builder = new System.Text.StringBuilder();
        var filler = "data: " + new string('x', 900) + "\n\n";
        while (builder.Length < (512 * 1024) + 100_000)
        {
            builder.Append(filler);
        }

        builder.Append("data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}\n\n");
        builder.Append("data: [DONE]\n\n");
        var payload = builder.ToString();

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "text/event-stream"),
        };

        await transformer.TransformResponseAsync(
            new DefaultHttpContext(),
            response,
            CancellationToken.None);
        _ = await response.Content.ReadAsStringAsync();

        // The trailing usage chunk survives because the streaming path retains the buffer tail.
        usageRecorder.Received(1).Enqueue(Arg.Is<Pol33.Core.Models.UsageEvent>(
            e => e.PromptTokens == 3 && e.CompletionTokens == 2));
        metrics.DidNotReceive().RecordUsageParseFailure(Arg.Any<string>());
    }

    /// <summary>
    /// A non-streaming body far larger than the head buffer must still be billed.
    /// </summary>
    /// <remarks>
    /// This previously recorded a parse failure and no usage at all, because only the head was
    /// retained and the truncated prefix could not be parsed as a document. Batch embeddings
    /// responses are routinely megabytes, which made "never billed" the normal case for them rather
    /// than an edge case. The trailing usage object is recovered from the retained tail.
    /// </remarks>
    [Fact]
    public async Task TransformResponseAsync_NonStreaming_WhenBodyExceedsCaptureLimit_StillRecordsUsage()
    {
        var usageRecorder = Substitute.For<IUsageRecorder>();
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var usageCapture = new InferenceUsageCapture(
            usageRecorder,
            metrics,
            canonicalModelId: "mock-gpt",
            requestId: "req-1",
            startedUtc: DateTimeOffset.UtcNow,
            tenant: null);

        var transformer = new StreamingHttpTransformer(
            isStreaming: false,
            clientModelName: null,
            canonicalModelId: "mock-gpt",
            usageCapture: usageCapture);

        var padding = new string('x', (512 * 1024) + 1);
        var payload = "{\"padding\":\"" + padding + "\",\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };

        await transformer.TransformResponseAsync(
            new DefaultHttpContext(),
            response,
            CancellationToken.None);

        var copiedBody = await response.Content.ReadAsStringAsync();
        copiedBody.Should().Be(payload);
        usageRecorder.Received(1).Enqueue(Arg.Is<Pol33.Core.Models.UsageEvent>(
            e => e.PromptTokens == 3 && e.CompletionTokens == 2));
        metrics.DidNotReceive().RecordUsageParseFailure(Arg.Any<string>());
    }
}
