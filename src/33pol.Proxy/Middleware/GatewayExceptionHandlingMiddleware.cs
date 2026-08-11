using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
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
            var code = ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? GatewayErrorCode.RequestTooLarge
                : GatewayErrorCode.InvalidJson;

            logger.LogWarning(
                "Rejected malformed request for {Method} {Path}: {Reason}",
                context.Request.Method,
                context.Request.Path,
                ex.Message);

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

            await WriteAsync(context, GatewayErrorCode.UpstreamError).ConfigureAwait(false);
        }
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
