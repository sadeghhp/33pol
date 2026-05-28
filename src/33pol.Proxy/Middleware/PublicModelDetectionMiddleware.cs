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
        var originalPosition = context.Request.Body.Position;

        try
        {
            var requestInfo = await InferenceRequestParser
                .ParseAsync(context.Request.Body, context.RequestAborted)
                .ConfigureAwait(false);

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
            // Leave items unset; auth and router enforce keys / return invalid JSON.
        }
        finally
        {
            context.Request.Body.Position = originalPosition;
        }

        await _next(context).ConfigureAwait(false);
    }
}
