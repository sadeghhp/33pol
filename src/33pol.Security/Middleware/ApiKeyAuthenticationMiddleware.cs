using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Security;
using Pol33.Security.Authentication;

namespace Pol33.Security.Middleware;

public sealed class ApiKeyAuthenticationMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly GatewayOptions _options;
    private readonly IApiKeyValidator _validator;

    public ApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        IApiKeyValidator validator)
    {
        _next = next;
        _options = options.Value;
        _validator = validator;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.IsAuthenticationEnabled || ApiKeyPathClassifier.IsPublicPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var policy = ApiKeyPathClassifier.IsAdminPath(context.Request.Path)
            ? ApiKeyPolicy.Admin
            : ApiKeyPolicy.Inference;

        if (policy == ApiKeyPolicy.Inference && !ApiKeyPathClassifier.RequiresInferenceKey(context.Request.Path) &&
            !ApiKeyPathClassifier.IsAdminPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (policy == ApiKeyPolicy.Admin && !ApiKeyPathClassifier.IsAdminPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var apiKey = ExtractApiKey(context.Request);
        var result = _validator.Validate(apiKey, policy);
        if (!result.IsSuccess)
        {
            await WriteUnauthorizedAsync(context, result.Status).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static string? ExtractApiKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization.ToString();
            const string prefix = "Bearer ";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[prefix.Length..].Trim();
            }
        }

        if (request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader))
        {
            return apiKeyHeader.ToString();
        }

        return null;
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, ApiKeyValidationStatus status)
    {
        var code = status == ApiKeyValidationStatus.Missing
            ? GatewayErrorCode.InvalidApiKey
            : GatewayErrorCode.InvalidApiKey;

        var message = status == ApiKeyValidationStatus.Missing
            ? "Missing API key"
            : "Invalid API key";

        var payload = ErrorResult.FromCode(code, message, "authentication_error", param: "authorization");
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        context.Response.Headers["X-33pol-Error-Code"] = payload.Error.Code;

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions)).ConfigureAwait(false);
    }
}
