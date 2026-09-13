using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Models;
using Pol33.Proxy.Middleware;

namespace Pol33.Proxy.Tests.Middleware;

/// <summary>
/// The pipeline had no terminal handler, so anything it did not catch was answered by Kestrel: a
/// bare status line with no body, no <c>error.code</c> and no <c>X-33pol-Error-Code</c> header.
/// </summary>
public sealed class GatewayExceptionHandlingMiddlewareTests
{
    /// <summary>
    /// An oversized body is only rejected up front when a Content-Length header declares it. A
    /// chunked upload — how most clients send a multi-megabyte body — is stopped by the server
    /// mid-read instead, and that failure has to produce the same documented error.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_PayloadTooLargeDuringBodyRead_WritesRequestTooLarge()
    {
        var context = CreateContext();

        await CreateMiddleware(_ => throw new BadHttpRequestException(
                "Request body too large.",
                StatusCodes.Status413PayloadTooLarge))
            .InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("request_too_large");
        (await ReadErrorCodeAsync(context)).Should().Be("request_too_large");
    }

    [Fact]
    public async Task InvokeAsync_MalformedRequest_WritesInvalidJson()
    {
        var context = CreateContext();

        await CreateMiddleware(_ => throw new BadHttpRequestException(
                "Invalid request line.",
                StatusCodes.Status400BadRequest))
            .InvokeAsync(context);

        (await ReadErrorCodeAsync(context)).Should().Be("invalid_json");
    }

    /// <summary>
    /// A client that declares a Content-Length and hangs up early, or trickles the body below the
    /// minimum data rate, sent nothing malformed. Labelling it invalid_json pointed the
    /// investigation at serialization instead of at the client's connection handling.
    /// </summary>
    [Theory]
    [InlineData("Unexpected end of request content.", StatusCodes.Status400BadRequest)]
    [InlineData("Reading the request body timed out due to data arriving too slowly. See MinRequestBodyDataRate.", StatusCodes.Status408RequestTimeout)]
    public async Task InvokeAsync_TruncatedOrStalledBody_WritesRequestIncomplete(string reason, int kestrelStatus)
    {
        var context = CreateContext();

        await CreateMiddleware(_ => throw new BadHttpRequestException(reason, kestrelStatus))
            .InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadErrorCodeAsync(context)).Should().Be("request_incomplete");
    }

    /// <summary>
    /// Kestrel's message says only that the body ended early. How many bytes arrived against how
    /// many were declared, and over how long, is what tells a truncating client from a proxy that
    /// buffers in pieces from a batch job that pauses between inputs — and a hundred and fifty such
    /// records had none of it.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_IncompleteBody_RecordsReceivedAgainstDeclaredBytesAndDuration()
    {
        var context = CreateContext();
        context.Request.Path = "/v1/embeddings";
        context.Request.Body = new MemoryStream(new byte[1_234]);
        context.Request.ContentLength = 98_765;
        var recorder = Substitute.For<IGatewayErrorRecorder>();

        await CreateMiddleware(
                _ => throw new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest),
                recorder)
            .InvokeAsync(context);

        (await ReadErrorCodeAsync(context)).Should().Be("request_incomplete");
        recorder.Received(1).Record(Arg.Is<GatewayErrorRecord>(r =>
            r.EventCode == "RequestIncomplete"
            && r.Message.StartsWith("Unexpected end of request content.")
            && r.Message.Contains("Received 1234 of 98765 declared request-body bytes")
            && r.Message.Contains(" ms.")
            && r.DurationMs != null
            && r.Path == "/v1/embeddings"));
    }

    /// <summary>A chunked upload declares no length; the record still says how much arrived.</summary>
    [Fact]
    public async Task InvokeAsync_IncompleteChunkedBody_RecordsReceivedBytesWithoutADeclaredTotal()
    {
        var context = CreateContext();
        context.Request.Body = new MemoryStream(new byte[512]);
        context.Request.ContentLength = null;
        var recorder = Substitute.For<IGatewayErrorRecorder>();

        await CreateMiddleware(
                _ => throw new BadHttpRequestException(
                    "Reading the request body timed out due to data arriving too slowly. See MinRequestBodyDataRate.",
                    StatusCodes.Status408RequestTimeout),
                recorder)
            .InvokeAsync(context);

        recorder.Received(1).Record(Arg.Is<GatewayErrorRecord>(r =>
            r.Message.Contains("Received 512 request-body bytes of a chunked body (no Content-Length)")));
    }

    /// <summary>
    /// Off the inference path the body is not buffered, so the count is unknown; the declared
    /// length is still worth stating. (The first cut of this read "an unknown number of of 98765".)
    /// </summary>
    [Fact]
    public async Task InvokeAsync_IncompleteUnbufferedBody_StatesTheDeclaredLengthOnly()
    {
        var context = CreateContext();
        context.Request.Path = "/admin/api/keys";
        context.Request.Body = new NonSeekableStream();
        context.Request.ContentLength = 98_765;
        var recorder = Substitute.For<IGatewayErrorRecorder>();

        await CreateMiddleware(
                _ => throw new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest),
                recorder)
            .InvokeAsync(context);

        recorder.Received(1).Record(Arg.Is<GatewayErrorRecord>(r =>
            r.Message.Contains("Received an unknown number of 98765 declared request-body bytes (body not buffered)")));
    }

    [Fact]
    public async Task InvokeAsync_UnexpectedException_WritesGatewayError()
    {
        var context = CreateContext();
        var recorder = Substitute.For<IGatewayErrorRecorder>();

        await CreateMiddleware(_ => throw new InvalidOperationException("boom"), recorder).InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        (await ReadErrorCodeAsync(context)).Should().Be("upstream_error");
        recorder.Received(1).Record(Arg.Is<GatewayErrorRecord>(r => r.Message == "boom" && r.DurationMs != null));
    }

    /// <summary>A client hanging up is not a gateway fault and has nobody left to report to.</summary>
    [Fact]
    public async Task InvokeAsync_ClientAborted_IsSwallowed()
    {
        var context = CreateContext();
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        context.RequestAborted = aborted.Token;

        await CreateMiddleware(_ => throw new OperationCanceledException(aborted.Token)).InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Length.Should().Be(0);
    }

    /// <summary>
    /// Nothing can be rewritten once bytes are on the wire, so the connection is aborted rather than
    /// a second, contradictory status being appended to a half-sent response.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ResponseAlreadyStarted_DoesNotAppendAnError()
    {
        var context = CreateContext();
        var responseFeature = new StartedResponseFeature();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(responseFeature);

        await CreateMiddleware(_ => throw new InvalidOperationException("mid-stream")).InvokeAsync(context);

        responseFeature.HasStarted.Should().BeTrue();
        context.Response.Body.Length.Should().Be(0);
    }

    private static GatewayExceptionHandlingMiddleware CreateMiddleware(
        RequestDelegate next,
        IGatewayErrorRecorder? recorder = null) =>
        new(
            next,
            new OpenAiErrorResponseWriter(),
            recorder ?? Substitute.For<IGatewayErrorRecorder>(),
            NullLogger<GatewayExceptionHandlingMiddleware>.Instance);

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StartedResponseFeature : Microsoft.AspNetCore.Http.Features.IHttpResponseFeature
    {
        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted => true;

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public string? ReasonPhrase { get; set; }

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }
    }
}
