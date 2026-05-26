using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Forwarding;
using Pol33.Proxy.Parsing;
using Pol33.Proxy.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Middleware;

public sealed class ModelRouterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IModelRegistry _registry;
    private readonly IBackendHealthStore _healthStore;
    private readonly IHttpForwarder _forwarder;
    private readonly HttpMessageInvoker _httpClient;
    private readonly ILogger<ModelRouterMiddleware> _logger;

    public ModelRouterMiddleware(
        RequestDelegate next,
        IModelRegistry registry,
        IBackendHealthStore healthStore,
        IHttpForwarder forwarder,
        HttpMessageInvoker httpClient,
        ILogger<ModelRouterMiddleware> logger)
    {
        _next = next;
        _registry = registry;
        _healthStore = healthStore;
        _forwarder = forwarder;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (InferenceRouteClassifier.IsPassthroughPath(context.Request.Path) ||
            !InferenceRouteClassifier.IsRoutableInference(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();

        InferenceRequestInfo requestInfo;
        try
        {
            requestInfo = await InferenceRequestParser.ParseAsync(context.Request.Body, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await OpenAiErrorResponses.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_request_error",
                "Invalid JSON in request body.").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(requestInfo.Model))
        {
            await OpenAiErrorResponses.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_request_error",
                "Missing required field: model.").ConfigureAwait(false);
            return;
        }

        if (!_registry.TryGetModel(requestInfo.Model, out var modelConfig) || modelConfig is null)
        {
            await OpenAiErrorResponses.WriteAsync(
                context,
                StatusCodes.Status404NotFound,
                "invalid_request_error",
                $"Model '{requestInfo.Model}' not found.").ConfigureAwait(false);
            return;
        }

        if (!_healthStore.IsBackendHealthy(modelConfig.Id))
        {
            await OpenAiErrorResponses.WriteAsync(
                context,
                StatusCodes.Status502BadGateway,
                "server_error",
                $"Backend for model '{modelConfig.Id}' is unhealthy.").ConfigureAwait(false);
            return;
        }

        context.Request.Body.Position = 0;

        var transformer = new StreamingHttpTransformer(
            requestInfo.Stream,
            requestInfo.Model,
            modelConfig.Id);

        var error = await _forwarder.SendAsync(
            context,
            modelConfig.Url,
            _httpClient,
            ForwarderRequestConfig.Empty,
            transformer).ConfigureAwait(false);

        if (error != ForwarderError.None && !context.Response.HasStarted)
        {
            _logger.LogWarning("Forwarder error {Error} for model {ModelId}", error, modelConfig.Id);
            await OpenAiErrorResponses.WriteAsync(
                context,
                StatusCodes.Status502BadGateway,
                "server_error",
                "Failed to forward request to backend.").ConfigureAwait(false);
        }
    }
}
