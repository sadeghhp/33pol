using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Pol33.Api.Security;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;

namespace Pol33.App.Metrics;

/// <summary>
/// Gates <c>/metrics</c>: the scrape is served when the caller presents the configured scrape token
/// as <c>Authorization: Bearer</c>, or an API key satisfying the Operator policy, or when
/// <see cref="GatewayMetricsOptions.AllowAnonymous"/> is set. Everything else is answered 401.
/// </summary>
/// <remarks>
/// A middleware rather than a route policy because the Prometheus exporter maps a raw request
/// delegate (endpoint filters do not run on it) and because the authentication handler treats
/// <c>/metrics</c> as an anonymous path, so <c>HttpContext.User</c> is never populated there. The
/// operator check therefore goes through <see cref="GatewayOperatorAccess"/>, which validates the
/// key itself and evaluates the same policy the admin routes use — including the "authentication
/// disabled" short-circuit, so a gateway with no keys serves the scrape as it serves everything.
///
/// The token comparison is constant-time. The token is only ever accepted from the
/// <c>Authorization</c> header, not from <c>X-API-Key</c>: it is a shared scraper secret, not a
/// gateway key, and it never reaches the key validator.
/// </remarks>
public sealed class MetricsScrapeAuthorizationMiddleware(
    RequestDelegate next,
    IOptions<GatewayMetricsOptions> options,
    IErrorResponseWriter errors)
{
    public const string MetricsPath = "/metrics";

    private static readonly PathString MetricsPathString = new(MetricsPath);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(MetricsPathString, StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (await IsAuthorizedAsync(context).ConfigureAwait(false))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var response = errors.Write(GatewayErrorCode.InvalidApiKey);
        context.Response.StatusCode = response.HttpStatusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers[GatewayHeaders.ErrorCode] = response.Body.Error.Code;
        await context.Response.WriteAsync(response.Json, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task<bool> IsAuthorizedAsync(HttpContext context)
    {
        var settings = options.Value;
        if (settings.AllowAnonymous)
        {
            return true;
        }

        if (settings.HasScrapeToken && PresentsScrapeToken(context.Request, settings.ScrapeToken!))
        {
            return true;
        }

        // Scoped: it wraps the (scoped) key validator.
        var operatorAccess = context.RequestServices.GetRequiredService<GatewayOperatorAccess>();
        return await operatorAccess.IsOperatorAsync(context, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool PresentsScrapeToken(HttpRequest request, string expected)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = authorization["Bearer ".Length..].Trim();
        if (presented.Length == 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));
    }
}
