using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Pol33.Proxy.Errors;

public static class OpenAiErrorResponses
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, int statusCode, string type, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var payload = new
        {
            error = new
            {
                message,
                type,
                param = (string?)null,
                code = (string?)null,
            },
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
