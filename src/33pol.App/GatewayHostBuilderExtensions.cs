using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Pol33.Api.DependencyInjection;
using Pol33.Api.Endpoints;
using Pol33.Core.Configuration;
using Pol33.Observability.Metrics;
using Pol33.Proxy.DependencyInjection;
using Pol33.Security.DependencyInjection;
using Serilog;

namespace Pol33.App;

public static class GatewayHostBuilderExtensions
{
    public static WebApplicationBuilder ConfigureGatewayHost(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, configuration) =>
            configuration.ReadFrom.Configuration(context.Configuration));

        builder.WebHost.ConfigureKestrel((context, options) =>
        {
            options.AllowSynchronousIO = false;
            options.AddServerHeader = false;
            options.Limits.MaxResponseBufferSize = null;

            var gatewayOptions = context.Configuration
                .GetSection(GatewayOptions.SectionName)
                .Get<GatewayOptions>() ?? new GatewayOptions();
            options.Limits.MaxRequestBodySize = gatewayOptions.Resilience.MaxRequestBodyBytes;
        });

        return builder;
    }

    public static WebApplication ConfigureGatewayPipeline(this WebApplication app)
    {
        // First in the pipeline: everything downstream — request logging, the audit trail, and above
        // all the anonymous rate-limit partition — reads the remote address, and each would otherwise
        // record the proxy instead of the caller.
        app.UseGatewayForwardedHeaders();

        app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestMethod", httpContext.Request.Method);
                diagnosticContext.Set("RequestPath", httpContext.Request.Path.Value ?? string.Empty);
                diagnosticContext.Set("StatusCode", httpContext.Response.StatusCode);
            };
        });

        // Inside request logging so Serilog records the status this handler settled on rather than
        // the exception, and outside everything else so no unhandled failure can reach Kestrel and be
        // answered with a bare status line instead of the documented error body.
        app.UseGatewayExceptionHandling();

        app.UseRouting();
        app.UseCors();
        app.UseGatewayRequestId();

        // Ahead of PublicModelDetection, which is the first thing to call EnableBuffering() and
        // parse the body. Registered after it, this middleware's own body-size cap could never fire
        // in time: an unauthenticated request was already buffered (spilling to a temp file past
        // 30 KB) and fully JSON-parsed before the limit it exists to enforce was applied. The drain
        // check belongs here for the same reason — a shutting-down gateway should reject before it
        // spends work on the body.
        app.UseInferenceResilience();
        app.UsePublicModelDetection();
        app.UseGatewaySecurity(app.Configuration);

        app.MapGet("/", GatewayEndpoints.GetRoot);
        app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));
        app.MapHealthChecks("/health/live");
        app.MapConfigAdminEndpoints();
        app.MapAdminRateLimitEndpoints();
        app.MapAdminCorsEndpoints();
        app.MapAdminKeyEndpoints();
        app.MapAdminModelGrantEndpoints();
        app.MapAdminControlPlaneEndpoints();
        app.MapAdminProviderEndpoints();
        app.MapAdminUsageEndpoints();
        app.MapMaintenanceAdminEndpoints();
        app.MapModelsEndpoints();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                if (ctx.Context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    ctx.Context.Response.Headers.Pragma = "no-cache";
                    AdminSecurityHeaders.Apply(ctx.Context.Response.Headers);
                }
            }
        });
        app.MapGatewayOperationsEndpoints();
        app.UseGatewayRateLimiting();
        app.UseGatewayQuotas();
        app.UseModelRouter();
        app.MapPrometheusScrapingEndpoint("/metrics");

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        return app;
    }

    /// <summary>
    /// Honours <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> from the proxies the operator has
    /// declared trustworthy, so the address the gateway partitions anonymous limits by is the
    /// caller's rather than the ingress's.
    /// </summary>
    private static void UseGatewayForwardedHeaders(this WebApplication app)
    {
        var options = app.Services
            .GetRequiredService<IOptions<GatewayOptions>>()
            .Value
            .ForwardedHeaders;

        if (!options.Enabled)
        {
            return;
        }

        const ForwardedHeaders headers = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = headers,
            ForwardLimit = options.ForwardLimit,
        };

        if (options.TrustAllProxies)
        {
            // Both collections default to loopback, and the middleware treats a non-empty collection
            // as an allow-list. Emptying them is what makes it accept the header from any peer.
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            app.Logger.LogWarning(
                "Forwarded headers are trusted from ANY peer (Gateway:ForwardedHeaders:TrustAllProxies). "
                + "Anything that can reach this port can choose the address its anonymous rate limits and "
                + "quotas are counted against. Restrict the port to your proxy, or name the proxy in "
                + "Gateway:ForwardedHeaders:KnownProxies/KnownNetworks instead.");
        }
        else
        {
            foreach (var proxy in options.GetKnownProxies())
            {
                forwarded.KnownProxies.Add(proxy);
            }

            foreach (var network in options.GetKnownNetworks())
            {
                forwarded.KnownIPNetworks.Add(network);
            }

            if (options.HasNoExplicitTrustAnchors)
            {
                app.Logger.LogWarning(
                    "Gateway:ForwardedHeaders:Enabled is true but no KnownProxies or KnownNetworks are "
                    + "configured, so only a proxy on loopback is trusted. A proxy on any other host — an "
                    + "ingress or a sidecar — will have its headers ignored and every anonymous caller will "
                    + "still share one rate-limit partition.");
            }
        }

        app.UseForwardedHeaders(forwarded);
    }
}
