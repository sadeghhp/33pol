using Microsoft.AspNetCore.Http;
using Pol33.Core.Errors;

namespace Pol33.Api.Middleware;

public sealed class RequestIdMiddleware
{
    private readonly RequestDelegate _next;

    public RequestIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = ResolveRequestId(context.Request);
        context.Items[RequestIdKeys.HttpContextItemKey] = requestId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[GatewayHeaders.RequestId] = requestId;
            return Task.CompletedTask;
        });

        await _next(context).ConfigureAwait(false);

        if (!context.Response.Headers.ContainsKey(GatewayHeaders.RequestId))
        {
            context.Response.Headers[GatewayHeaders.RequestId] = requestId;
        }
    }

    public static string ResolveRequestId(HttpRequest request)
    {
        if (request.Headers.TryGetValue(GatewayHeaders.RequestId, out var incoming))
        {
            var value = incoming.ToString().Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return $"req_{Guid.NewGuid():N}";
    }
}
