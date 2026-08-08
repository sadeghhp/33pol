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
        app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestMethod", httpContext.Request.Method);
                diagnosticContext.Set("RequestPath", httpContext.Request.Path.Value ?? string.Empty);
                diagnosticContext.Set("StatusCode", httpContext.Response.StatusCode);
            };
        });

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
}
