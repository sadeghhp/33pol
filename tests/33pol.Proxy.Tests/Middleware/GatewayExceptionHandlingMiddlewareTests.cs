using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Errors;
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
                "Unexpected end of request content.",
                StatusCodes.Status400BadRequest))
            .InvokeAsync(context);

        (await ReadErrorCodeAsync(context)).Should().Be("invalid_json");
    }

    [Fact]
    public async Task InvokeAsync_UnexpectedException_WritesGatewayError()
    {
        var context = CreateContext();

        await CreateMiddleware(_ => throw new InvalidOperationException("boom")).InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        (await ReadErrorCodeAsync(context)).Should().Be("upstream_error");
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

    private static GatewayExceptionHandlingMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next, new OpenAiErrorResponseWriter(), NullLogger<GatewayExceptionHandlingMiddleware>.Instance);

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
