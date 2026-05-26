using Microsoft.AspNetCore.Http;

namespace Pol33.Security.Middleware;

public sealed class RequestIdMiddleware
{
    public const string RequestIdHeaderName = "X-Request-Id";
    public const string RequestIdItemKey = "33pol.RequestId";

    private readonly RequestDelegate _next;

    public RequestIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.Request.Headers[RequestIdHeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requestId))
        {
            requestId = Guid.NewGuid().ToString("N");
        }

        context.Items[RequestIdItemKey] = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RequestIdHeaderName] = requestId;
            return Task.CompletedTask;
        });

        await _next(context).ConfigureAwait(false);
    }
}
