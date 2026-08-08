using Microsoft.AspNetCore.Http;
using Pol33.Core.Errors;

namespace Pol33.Api.Middleware;

public sealed class RequestIdMiddleware
{
    private readonly RequestDelegate _next;

    public RequestIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var serverRequestId = $"req_{Guid.NewGuid():N}";
        context.Items[RequestIdKeys.HttpContextItemKey] = serverRequestId;

        var echoId = serverRequestId;
        if (context.Request.Headers.TryGetValue(GatewayHeaders.RequestId, out var incoming))
        {
            var clientValue = incoming.ToString().Trim();
            if (!string.IsNullOrEmpty(clientValue))
            {
                echoId = clientValue;
            }
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[GatewayHeaders.RequestId] = echoId;
            return Task.CompletedTask;
        });

        await _next(context).ConfigureAwait(false);

        if (!context.Response.Headers.ContainsKey(GatewayHeaders.RequestId))
        {
            context.Response.Headers[GatewayHeaders.RequestId] = echoId;
        }
    }
}
