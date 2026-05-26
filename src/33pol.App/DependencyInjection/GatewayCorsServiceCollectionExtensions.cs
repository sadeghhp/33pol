using Microsoft.Extensions.Hosting;
using Pol33.Core.Configuration;

namespace Pol33.App.DependencyInjection;

public static class GatewayCorsServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayCors(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var corsOptions = configuration
            .GetSection($"{GatewayOptions.SectionName}:{GatewayCorsOptions.SectionName}")
            .Get<GatewayCorsOptions>() ?? new GatewayCorsOptions();

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (environment.IsDevelopment())
                {
                    policy.AllowAnyOrigin()
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                    return;
                }

                if (corsOptions.AllowedOrigins.Length > 0)
                {
                    policy.WithOrigins(corsOptions.AllowedOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                }
            });
        });

        return services;
    }
}
