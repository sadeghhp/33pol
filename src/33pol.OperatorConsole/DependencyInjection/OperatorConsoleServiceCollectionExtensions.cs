using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Configuration;
using Pol33.OperatorConsole.Hosting;

namespace Pol33.OperatorConsole.DependencyInjection;

public static class OperatorConsoleServiceCollectionExtensions
{
    public static IServiceCollection AddOperatorConsole(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<OperatorConsoleOptions>()
            .Bind(configuration.GetSection("Gateway:OperatorConsole"))
            .Validate(
                o => o.RefreshIntervalMs is >= 250 and <= 60_000,
                "Gateway:OperatorConsole:RefreshIntervalMs must be between 250 and 60000.");

        services.AddHostedService<OperatorConsoleHostedService>();
        return services;
    }
}
