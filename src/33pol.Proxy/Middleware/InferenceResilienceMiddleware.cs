using Microsoft.AspNetCore.Http;
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

        await _next(context).ConfigureAwait(false);
    }
}
