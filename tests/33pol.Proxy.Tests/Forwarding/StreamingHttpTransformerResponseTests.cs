using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.Forwarding;

public sealed class StreamingHttpTransformerResponseTests
{
    [Fact]
    public async Task TransformResponseAsync_Streaming_DelegatesHeadersToForwarder()
    {
        var transformer = new StreamingHttpTransformer(isStreaming: true, "alias", "canonical");
        var context = new DefaultHttpContext();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: hello\n\n", Encoding.UTF8, "text/event-stream"),
        };

        var shouldCopy = await transformer.TransformResponseAsync(
            context,
            response,
            CancellationToken.None);

        shouldCopy.Should().BeTrue();
        context.Response.Headers.ContainsKey("Content-Type").Should().BeFalse();
        context.Response.Headers.ContainsKey("Content-Length").Should().BeFalse();
    }

    /// <summary>
    /// Wrapping the body for usage capture must not lose the upstream's content headers on the way
    /// to the client. Only Content-Length goes, because the wrapper cannot vouch for it.
    /// </summary>
    [Fact]
    public async Task TransformResponseAsync_UsageCapture_PreservesContentHeadersExceptLength()
    {
        var (transformer, _, _) = CreateWithUsageCapture(isStreaming: false);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"usage":{"prompt_tokens":1,"completion_tokens":1}}""", Encoding.UTF8, "application/json"),
        };
        response.Content.Headers.ContentLanguage.Add("en");
        response.Content.Headers.TryAddWithoutValidation("Content-Disposition", "inline");
        var originalLength = response.Content.Headers.ContentLength;
        originalLength.Should().NotBeNull();

        await transformer.TransformResponseAsync(new DefaultHttpContext(), response, CancellationToken.None);

        response.Content.Should().BeOfType<StreamContent>();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
        response.Content.Headers.ContentLanguage.Should().ContainSingle().Which.Should().Be("en");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
        response.Content.Headers.ContentLength.Should().NotBe(originalLength);
    }

    /// <summary>
    /// A compressed body is not JSON on the wire. It is relayed untouched — including the
    /// Content-Encoding the client needs to decode it — and usage capture steps aside instead of
    /// parsing gzip bytes and counting the inevitable failure against the backend.
    /// </summary>
    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task TransformResponseAsync_UsageCapture_SkipsEncodedBodies(string encoding)
    {
        var (transformer, recorder, metrics) = CreateWithUsageCapture(isStreaming: false);
        var original = new ByteArrayContent([0x1f, 0x8b, 0x08, 0x00]);
        original.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        original.Headers.ContentEncoding.Add(encoding);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = original };

        await transformer.TransformResponseAsync(new DefaultHttpContext(), response, CancellationToken.None);
        var relayed = await response.Content.ReadAsByteArrayAsync();
        response.Dispose();

        response.Content.Should().BeSameAs(original);
        relayed.Should().Equal(0x1f, 0x8b, 0x08, 0x00);
        recorder.DidNotReceive().Enqueue(Arg.Any<UsageEvent>());
        metrics.Received(1).RecordUsageParseFailure("canonical");
    }

    [Fact]
    public async Task TransformResponseAsync_UsageCapture_IdentityEncodingStillParses()
    {
        var (transformer, recorder, _) = CreateWithUsageCapture(isStreaming: false);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"usage":{"prompt_tokens":2,"completion_tokens":3}}""", Encoding.UTF8, "application/json"),
        };
        response.Content.Headers.ContentEncoding.Add("identity");

        await transformer.TransformResponseAsync(new DefaultHttpContext(), response, CancellationToken.None);
        _ = await response.Content.ReadAsByteArrayAsync();
        response.Dispose();

        recorder.Received(1).Enqueue(Arg.Is<UsageEvent>(e => e.PromptTokens == 2 && e.CompletionTokens == 3));
    }

    private static (StreamingHttpTransformer Transformer, IUsageRecorder Recorder, IGatewayMetricsCollector Metrics)
        CreateWithUsageCapture(bool isStreaming)
    {
        var recorder = Substitute.For<IUsageRecorder>();
        recorder.Enqueue(Arg.Any<UsageEvent>()).Returns(true);
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var capture = new InferenceUsageCapture(
            recorder,
            metrics,
            "canonical",
            "req-1",
            DateTimeOffset.UtcNow,
            tenant: null);
        var transformer = new StreamingHttpTransformer(isStreaming, "alias", "canonical", capture);
        return (transformer, recorder, metrics);
    }
}
