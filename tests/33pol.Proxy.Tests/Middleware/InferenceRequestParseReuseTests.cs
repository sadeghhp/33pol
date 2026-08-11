using System.Text;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Proxy.Middleware;
using Pol33.Proxy.Parsing;

namespace Pol33.Proxy.Tests.Middleware;

/// <summary>
/// The request body is parsed exactly once per request.
/// </summary>
/// <remarks>
/// Both PublicModelDetectionMiddleware and ModelRouterMiddleware need the same three routing
/// scalars, and each used to parse the whole body for itself — doubling the most expensive step on
/// the inference path. The first of the two also runs ahead of authentication, so an unauthenticated
/// caller paid for both parses before any credential was checked.
/// </remarks>
public sealed class InferenceRequestParseReuseTests
{
    [Fact]
    public async Task PublicModelDetection_PublishesTheParseForLaterMiddleware()
    {
        var context = CreateContext("""{"model":"gpt","stream":true,"max_tokens":128}""");

        await CreateDetectionMiddleware().InvokeAsync(context);

        InferenceRequestParseCache.TryGet(context, out var info).Should().BeTrue();
        info.Should().NotBeNull();
        info!.Value.Model.Should().Be("gpt");
        info.Value.Stream.Should().BeTrue();
        info.Value.MaxTokens.Should().Be(128);
        info.Value.ModelValueRange.Should().NotBeNull();
    }

    /// <summary>
    /// Proves reuse rather than merely asserting a cached value: the body is swapped for a stream
    /// that throws if anything reads it, so a second parse cannot go unnoticed.
    /// </summary>
    [Fact]
    public async Task DownstreamMiddleware_DoesNotReadTheBodyAgain()
    {
        var context = CreateContext("""{"model":"gpt","stream":false}""");

        await CreateDetectionMiddleware().InvokeAsync(context);
        context.Request.Body = new ThrowOnReadStream();

        InferenceRequestParseCache.TryGet(context, out var info).Should().BeTrue();
        info!.Value.Model.Should().Be("gpt");
    }

    [Fact]
    public async Task MalformedBody_IsCachedAsInvalidSoItIsNotReparsed()
    {
        var context = CreateContext("""{"model":"gpt",""");

        await CreateDetectionMiddleware().InvokeAsync(context);

        InferenceRequestParseCache.TryGet(context, out var info).Should().BeTrue();
        info.Should().BeNull("an unparseable body is recorded, not left for the router to retry");
    }

    [Fact]
    public async Task PassthroughRequest_LeavesNoCacheEntry()
    {
        var context = CreateContext("""{"model":"gpt"}""");
        context.Request.Path = "/health/live";
        context.Request.Method = HttpMethods.Get;

        await CreateDetectionMiddleware().InvokeAsync(context);

        InferenceRequestParseCache.TryGet(context, out _).Should().BeFalse();
    }

    [Fact]
    public async Task PublicModelDetection_RewindsTheBodyForForwarding()
    {
        const string body = """{"model":"gpt","stream":false}""";
        var context = CreateContext(body);

        await CreateDetectionMiddleware().InvokeAsync(context);

        context.Request.Body.Position.Should().Be(0);
        context.Request.Body.Length.Should().Be(Encoding.UTF8.GetByteCount(body));
    }

    private static PublicModelDetectionMiddleware CreateDetectionMiddleware()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel(Arg.Any<string>(), out Arg.Any<ModelConfig?>()).Returns(false);
        return new PublicModelDetectionMiddleware(_ => Task.CompletedTask, registry);
    }

    private static DefaultHttpContext CreateContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class ThrowOnReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The request body must not be read a second time.");

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The request body must not be read a second time.");
    }
}
