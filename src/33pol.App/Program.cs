using Pol33.App;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddGatewayCore(builder.Configuration)
    .AddGatewayHealthChecks();

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseRouting();

app.MapGet("/", GatewayEndpoints.GetRoot);
app.MapHealthChecks("/health/live");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();
