using Microsoft.AspNetCore.Http;
using Pol33.Core.Errors;

namespace Pol33.Proxy.Errors;

public static class GatewayHttpErrorExtensions
{
    public static Task WriteGatewayErrorAsync(
        this HttpContext context,
        WrittenErrorResponse response,
        CancellationToken cancellationToken = default,
        int? retryAfterSeconds = null)
    {
        context.Response.StatusCode = response.HttpStatusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers[GatewayHeaders.ErrorCode] = response.Body.Error.Code;

        if (retryAfterSeconds is > 0)
        {
            context.Response.Headers[GatewayHeaders.RetryAfter] = retryAfterSeconds.Value.ToString();
        }

        return context.Response.WriteAsync(response.Json, cancellationToken);
    }
}
