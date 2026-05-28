using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Policy.Admin;
using Pol33.Policy.Quotas;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.DependencyInjection;

public static class PolicyServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayPolicy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName));

        services
            .AddOptions<QuotaOptions>()
            .Bind(configuration.GetSection(QuotaOptions.SectionName));

        services.AddSingleton<IRateLimitPolicyResolver, RateLimitPolicyResolver>();
        services.AddSingleton<IRateLimitConfigAdminService, RateLimitConfigAdminService>();
        services.AddSingleton<IDistributedRateLimitStore, InMemoryDistributedRateLimitStore>();
        services.AddSingleton<IQuotaService, InMemoryQuotaService>();

        return services;
    }
}
