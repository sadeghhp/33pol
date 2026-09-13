using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Diagnostics;
using Pol33.Core.Errors;
using Pol33.Core.Models;
using Pol33.Core.Security;
using Pol33.Proxy.Errors;

namespace Pol33.Proxy.Middleware;

/// <summary>
/// Turns an unhandled failure into the same OpenAI-shaped error body every other gateway rejection
/// produces.
/// </summary>
/// <remarks>
/// <para>Without this the pipeline had no terminal handler, so anything it did not catch was
/// answered by Kestrel: a bare status line with no body, no <c>error.code</c> and no
/// <c>X-33pol-Error-Code</c> header. The case that reached clients in practice was an oversized
/// request body. <c>InferenceResilienceMiddleware</c> answers <c>request_too_large</c> only when a
/// <c>Content-Length</c> header declares the size up front; a chunked upload — how most clients send
/// a multi-megabyte body — is instead stopped by the server mid-read, which throws
/// <see cref="BadHttpRequestException"/> from inside the JSON parse. Neither parse site caught it,
/// so the same condition produced a documented 400 with a body or an opaque 413 without one,
/// depending only on how the client framed its request.</para>
///
/// <para>Once the response has started nothing can be rewritten, so the connection is aborted
/// instead: a truncated body with a reset is the only honest signal left, and it is what a client
/// needs to see rather than a stream that simply stops.</para>
/// </remarks>
public sealed class GatewayExceptionHandlingMiddleware(
    RequestDelegate next,
    IErrorResponseWriter errors,
    IGatewayErrorRecorder errorRecorder,
    ILogger<GatewayExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // This handler sits at the top of the pipeline, so its clock is as close to request start
        // as the gateway can measure. Without it a body-read failure was recorded with no duration
        // at all, and "did the client hang up at once or after a minute of trickling" — the
        // question that separates a client-side timeout from a network fault — went unanswered.
        var started = Stopwatch.GetTimestamp();

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex)
        {
            var code = ClassifyBadRequest(ex);
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var message = code == GatewayErrorCode.RequestIncomplete
                ? DescribeIncompleteBody(context, ex, durationMs)
                : ex.Message;

            logger.LogWarning(
                "Rejected malformed request for {Method} {Path}: {Reason}",
                context.Request.Method,
                context.Request.Path,
                message);

            RecordError(context, ex, GatewayLogLevel.Warning, code.ToString(), StatusCodeFor(code), message, durationMs);
            await WriteAsync(context, code).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away. Nothing to report to it, and it is not a gateway fault.
            logger.LogDebug(
                "Client aborted {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unhandled exception while processing {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            RecordError(
                context,
                ex,
                GatewayLogLevel.Error,
                GatewayErrorCode.UpstreamError.ToString(),
                StatusCodes.Status502BadGateway,
                ex.Message,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await WriteAsync(context, GatewayErrorCode.UpstreamError).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes an unhandled failure to the error store. This is the capture point for everything
    /// off the inference path — admin routes, model listings, health — which the proxy's own
    /// recording never sees.
    /// </summary>
    private void RecordError(
        HttpContext context,
        Exception exception,
        GatewayLogLevel level,
        string eventCode,
        int statusCode,
        string message,
        double durationMs)
    {
        // The inference path records its own failures with the model, upstream and outcome
        // attached. Recording again here would add a second, thinner row for the same fault.
        if (context.Items.ContainsKey(GatewayErrorContextKeys.ErrorCaptured))
        {
            return;
        }

        errorRecorder.Record(new GatewayErrorRecord
        {
            Id = $"err_{Guid.NewGuid():N}",
            Fingerprint = string.Empty,
            OccurredAt = DateTimeOffset.UtcNow,
            Level = level.ToString(),
            Source = GatewayErrorSourceNames.Exception,
            Category = nameof(GatewayExceptionHandlingMiddleware),
            EventCode = eventCode,
            Message = message,
            ExceptionType = exception.GetType().FullName,
            StackTrace = exception.ToString(),
            Method = context.Request.Method,
            Path = context.Request.Path.Value,
            RouteKind = ClassifyRouteKind(context.Request.Path),
            StatusCode = statusCode,
            TenantId = context.User.FindFirst(GatewayAuthClaims.TenantId)?.Value,
            ApiKeyId = context.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
            RequestId = context.Items.TryGetValue(RequestIdKeys.HttpContextItemKey, out var id)
                ? id?.ToString()
                : null,
            DurationMs = durationMs,
            Hint = GatewayLogHints.ForException(exception),
        });

        context.Items[GatewayErrorContextKeys.ErrorCaptured] = true;
    }

    /// <summary>
    /// Kestrel raises the same exception type for a body that never parsed and for one that never
    /// fully arrived. Reporting the latter as <c>invalid_json</c> sent operators hunting for a
    /// serialization bug when the client had simply hung up mid-upload or stalled below
    /// <c>MinRequestBodyDataRate</c>.
    /// </summary>
    private static GatewayErrorCode ClassifyBadRequest(BadHttpRequestException ex)
    {
        if (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return GatewayErrorCode.RequestTooLarge;
        }

        if (ex.StatusCode == StatusCodes.Status408RequestTimeout
            || ex.Message.Contains("Unexpected end of request content", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("arriving too slowly", StringComparison.OrdinalIgnoreCase))
        {
            return GatewayErrorCode.RequestIncomplete;
        }

        return GatewayErrorCode.InvalidJson;
    }

    /// <summary>
    /// Kestrel's message says only that the body ended early or arrived too slowly. How many bytes
    /// arrived against how many were declared, and over how long, is what tells a truncating client
    /// from a proxy that buffers in pieces from a batch job that pauses between inputs — and none
    /// of it is recoverable after the fact, so it is put in the message here.
    /// </summary>
    /// <remarks>
    /// The counts are plain digits: the fingerprint normalizes numbers away, so every occurrence
    /// still groups as one fault while each keeps its own figures.
    /// </remarks>
    private static string DescribeIncompleteBody(HttpContext context, BadHttpRequestException ex, double durationMs)
    {
        var received = TryGetBufferedBodyLength(context.Request.Body);
        var declared = context.Request.ContentLength;

        var progress = (received, declared) switch
        {
            (long r, long d) => string.Create(CultureInfo.InvariantCulture, $"Received {r} of {d} declared request-body bytes"),
            (long r, null) => string.Create(CultureInfo.InvariantCulture, $"Received {r} request-body bytes of a chunked body (no Content-Length)"),
            (null, long d) => string.Create(CultureInfo.InvariantCulture, $"Received an unknown number of {d} declared request-body bytes (body not buffered)"),
            (null, null) => "Received an unknown number of request-body bytes of a chunked body (not buffered, no Content-Length)",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ex.Message.TrimEnd()} {progress} over {durationMs:F0} ms.");
    }

    /// <summary>
    /// Bytes buffered so far by the request-body stream. The inference pipeline enables buffering
    /// before the first read, and the buffering stream's length is exactly what has arrived.
    /// Null for an unbuffered body, whose read position is not recoverable.
    /// </summary>
    private static long? TryGetBufferedBodyLength(Stream body)
    {
        try
        {
            return body.CanSeek ? body.Length : null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            return null;
        }
    }

    private static int StatusCodeFor(GatewayErrorCode code) =>
        code == GatewayErrorCode.RequestTooLarge
            ? StatusCodes.Status413PayloadTooLarge
            : StatusCodes.Status400BadRequest;

    /// <summary>
    /// Coarse route classification for fingerprinting. Raw paths carry tenant- and model-specific
    /// segments that would split one fault into a group per caller.
    /// </summary>
    private static string ClassifyRouteKind(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
        {
            return "admin";
        }

        return value.StartsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "inference" : "other";
    }

    private async Task WriteAsync(HttpContext context, GatewayErrorCode code)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Clear();

        // CancellationToken.None: the client's token may already be cancelled, and the point of this
        // handler is that the error still reaches whatever is still listening.
        await context.WriteGatewayErrorAsync(errors.Write(code), CancellationToken.None).ConfigureAwait(false);
    }
}
