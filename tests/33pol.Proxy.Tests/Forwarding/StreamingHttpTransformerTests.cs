using System.Net;
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

    [Theory]
    [InlineData("{\"model\":\"alias\"}", "canonical")]
    [InlineData("{\"model\": \"alias\"}", "canonical")]
    public void RewriteModelProperty_AliasSpacingVariants_RewritesCanonicalId(string json, string canonical)
    {
        var rewritten = StreamingHttpTransformer.RewriteModelProperty(json, canonical);

        rewritten.Should().Contain($"\"model\":\"{canonical}\"");
        rewritten.Should().NotContain("alias");
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
    public async Task TransformResponseAsync_NonStreaming_WhenBodyExceedsCaptureLimit_RecordsParseFailure()
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
        usageRecorder.DidNotReceive().Enqueue(Arg.Any<Pol33.Core.Models.UsageEvent>());
        metrics.Received(1).RecordUsageParseFailure("mock-gpt");
    }
}
