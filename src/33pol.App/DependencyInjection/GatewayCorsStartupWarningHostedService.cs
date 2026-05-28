using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pol33.Core.Configuration;

namespace Pol33.App.DependencyInjection;

internal sealed class GatewayCorsStartupWarningHostedService(
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<GatewayCorsStartupWarningHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            return Task.CompletedTask;
        }

        var section = configuration.GetSection($"{GatewayOptions.SectionName}:{GatewayCorsOptions.SectionName}");
        var corsOptions = section.Get<GatewayCorsOptions>() ?? new GatewayCorsOptions();
        var origins = corsOptions.GetNormalizedOrigins();
        if (origins.Length == 0)
        {
            logger.LogWarning(
                "Production CORS: no Gateway:Cors:AllowedOrigins configured; browser cross-origin clients will be blocked.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
