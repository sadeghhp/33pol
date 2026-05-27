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
        app.UseGatewaySecurity(app.Configuration);

        app.MapGet("/", GatewayEndpoints.GetRoot);
        app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));
        app.MapHealthChecks("/health/live");
        app.MapConfigAdminEndpoints();
        app.MapAdminKeyEndpoints();
        app.MapAdminControlPlaneEndpoints();
        app.MapAdminProviderEndpoints();
        app.MapAdminUsageEndpoints();
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
                }
            }
        });
        app.MapGatewayOperationsEndpoints();
        app.UseInferenceResilience();
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
