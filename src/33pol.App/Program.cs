using Pol33.App;
using Pol33.App.Hosting;
using Serilog;

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder
        .AddGatewaySerilog()
        .ConfigureGatewayKestrel();

    builder.Services
        .AddGatewayCore(builder.Configuration)
        .AddGatewayHealthChecks();

    builder.Services.AddOpenApi();

    var app = builder.Build();
    app.UseGatewayPipeline();
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Gateway host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
