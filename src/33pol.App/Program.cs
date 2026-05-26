using Pol33.Api.Endpoints;
using Pol33.App;
using Pol33.Proxy.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddGatewayCore(builder.Configuration)
    .AddGatewayHealthChecks();

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseRouting();

app.MapGet("/", GatewayEndpoints.GetRoot);
app.MapHealthChecks("/health/live");
app.MapConfigAdminEndpoints();
app.MapModelsEndpoints();
app.MapGatewayOperationsEndpoints();
app.UseModelRouter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();
