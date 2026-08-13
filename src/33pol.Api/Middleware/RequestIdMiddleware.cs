using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pol33.Core.Diagnostics;
using Pol33.Core.Errors;

namespace Pol33.Api.Middleware;

public sealed class RequestIdMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

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

        // Opening a logging scope here is what puts a request id on the diagnostics an operator
        // reads. The Logs tab has a Request ID column and the admin log sink has a field for it, but
        // nothing ever populated either — a scope is the only mechanism by which a warning logged
        // three layers down can know which request it belongs to.
        //
        // The server-minted id, not the echoed one: a client-supplied value correlates the client's
        // view, but every gateway-side record keys off the id the gateway generated.
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            [GatewayLogScopeKeys.RequestId] = serverRequestId,
        });

        await _next(context).ConfigureAwait(false);

        if (!context.Response.Headers.ContainsKey(GatewayHeaders.RequestId))
        {
            context.Response.Headers[GatewayHeaders.RequestId] = echoId;
        }
    }
}
