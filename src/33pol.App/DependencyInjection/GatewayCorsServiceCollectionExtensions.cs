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

        var allowedOrigins = corsOptions.GetNormalizedOrigins();

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

                if (allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                    return;
                }

                policy.SetIsOriginAllowed(_ => false);
            });
        });

        services.AddHostedService<GatewayCorsStartupWarningHostedService>();

        return services;
    }
}
