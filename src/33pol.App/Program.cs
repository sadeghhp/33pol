using Pol33.App;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigureGatewayHost();

builder.Services
    .AddGatewayCore(builder.Configuration, builder.Environment)
    .AddGatewayHealthChecks();

builder.Services.AddOpenApi();

var app = builder.Build();
app.ConfigureGatewayPipeline();

try
{
    Log.Information("Starting 33pol gateway");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Gateway host terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
