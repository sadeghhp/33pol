using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Proxy.Parsing;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Middleware;

public sealed class PublicModelDetectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IModelRegistry _registry;

    public PublicModelDetectionMiddleware(RequestDelegate next, IModelRegistry registry)
    {
        _next = next;
        _registry = registry;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!InferenceRouteClassifier.IsRoutableInference(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();

        // Parse from the start of the body unconditionally: the byte offsets the parser reports are
        // relative to where it began reading, and every consumer of them seeks back to 0.
        context.Request.Body.Position = 0;

        try
        {
            var requestInfo = await InferenceRequestParser
                .ParseAsync(context.Request.Body, context.RequestAborted)
                .ConfigureAwait(false);

            // Published for the router, which needs the same three scalars and must not pay for a
            // second parse of the same body.
            InferenceRequestParseCache.SetParsed(context, requestInfo);

            if (!string.IsNullOrWhiteSpace(requestInfo.Model) &&
                _registry.TryGetModel(requestInfo.Model, out var modelConfig) &&
                modelConfig is not null &&
                modelConfig.AllowsPublicGatewayAccess())
            {
                context.Items[PublicModelAccessKeys.IsPublicInference] = true;
                context.Items[PublicModelAccessKeys.CanonicalModelId] = modelConfig.Id;
            }
        }
        catch (JsonException)
        {
            // Leave items unset; auth and router enforce keys / return invalid JSON. Recording the
            // failure keeps the router from re-parsing a body already known to be malformed.
            InferenceRequestParseCache.SetInvalidJson(context);
        }
        finally
        {
            // Guarded so a failure that aborted the body read (an oversized payload, above all)
            // propagates to the exception middleware instead of being masked by a seek that throws.
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
