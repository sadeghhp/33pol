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
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex)
        {
            var code = ClassifyBadRequest(ex);

            logger.LogWarning(
                "Rejected malformed request for {Method} {Path}: {Reason}",
                context.Request.Method,
                context.Request.Path,
                ex.Message);

            RecordError(context, ex, GatewayLogLevel.Warning, code.ToString(), StatusCodeFor(code));
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
                StatusCodes.Status502BadGateway);
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
        int statusCode)
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
            Message = exception.Message,
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
