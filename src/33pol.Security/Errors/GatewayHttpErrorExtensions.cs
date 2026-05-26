using Microsoft.AspNetCore.Http;
using Pol33.Core.Errors;

namespace Pol33.Security.Errors;

internal static class GatewayHttpErrorExtensions
{
    public static Task WriteGatewayErrorAsync(
        this HttpContext context,
        WrittenErrorResponse response,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = response.HttpStatusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers[GatewayHeaders.ErrorCode] = response.Body.Error.Code;

        return context.Response.WriteAsync(response.Json, cancellationToken);
    }
}
