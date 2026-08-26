using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Middleware;

public sealed class InferenceResilienceMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IGatewayDrainState _drainState;
    private readonly IErrorResponseWriter _errors;
    private readonly long _maxRequestBodyBytes;
    private readonly int _requestBufferThresholdBytes;

    public InferenceResilienceMiddleware(
        RequestDelegate next,
        IGatewayDrainState drainState,
        IErrorResponseWriter errors,
        IOptions<GatewayOptions> options)
    {
        _next = next;
        _drainState = drainState;
        _errors = errors;
        _maxRequestBodyBytes = options.Value.Resilience.MaxRequestBodyBytes;
        _requestBufferThresholdBytes = options.Value.Resilience.RequestBufferThresholdBytes;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!InferenceRouteClassifier.IsRoutableInference(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (_drainState.IsDraining)
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.GatewayDraining),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength is > 0 and long contentLength &&
            contentLength > _maxRequestBodyBytes)
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.RequestTooLarge),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // Enforce the cap during body read as well, so a chunked request with no Content-Length header
        // cannot bypass the check above and buffer an unbounded body (EnableBuffering + JSON parse) into
        // memory/disk. The server enforces this while the body is streamed.
        var maxBodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBodySizeFeature is { IsReadOnly: false })
        {
            maxBodySizeFeature.MaxRequestBodySize = _maxRequestBodyBytes;
        }

        // Buffering is turned on here, ahead of every consumer, so the threshold is applied once and
        // from configuration. EnableBuffering leaves an already-seekable body alone, so the calls
        // downstream (PublicModelDetection, ModelRouter) stay correct as no-ops and still cover a
        // pipeline where this middleware did not run.
        //
        // The threshold matters because the default is 30 KB: below it the body stays in memory,
        // above it every read and replay of the body goes through a temp file, and this gateway
        // reads the body two or three times per request.
        context.Request.EnableBuffering(_requestBufferThresholdBytes);

        await _next(context).ConfigureAwait(false);
    }
}
