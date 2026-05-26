using Pol33.Api.Endpoints;
using Pol33.App;
using Pol33.Proxy.DependencyInjection;
using Pol33.Security.DependencyInjection;

namespace Pol33.App.Hosting;

public static class GatewayPipelineExtensions
{
    /// <summary>
    /// Phase 2–3 pipeline: Serilog → routing → request id → API key auth → minimal APIs → model router.
    /// </summary>
    public static WebApplication UseGatewayPipeline(this WebApplication app)
    {
        app.UseGatewaySerilogRequestLogging();
        app.UseRouting();
        app.UseGatewayRequestId();
        app.UseGatewayApiKeyAuthentication();
        app.MapGatewayEndpoints();
        app.UseModelRouter();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        return app;
    }

    public static WebApplication MapGatewayEndpoints(this WebApplication app)
    {
        app.MapGet("/", GatewayEndpoints.GetRoot);
        app.MapHealthChecks("/health/live");
        app.MapConfigAdminEndpoints();
        app.MapModelsEndpoints();
        app.MapGatewayOperationsEndpoints();
        return app;
    }
}
