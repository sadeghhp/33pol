using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Api.Middleware;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;

namespace Pol33.Api.DependencyInjection;

public static class GatewayApiServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayApi(this IServiceCollection services)
    {
        services.AddSingleton<IErrorResponseWriter, OpenAiErrorResponseWriter>();
        services.AddSingleton<GatewayProcessClock>();
        services.AddScoped<ModelsApiService>();
        services.AddSingleton<GatewayHealthService>();
        services.AddSingleton<GatewayReadinessService>();
        services.AddSingleton<GatewayStatsService>();
        services.AddSingleton(sp => Core.Providers.UpstreamEnvVarPolicy.FromOptions(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Configuration.GatewayOptions>>().Value));
        services.AddSingleton<AdminModelProvisioningService>();
        services.AddSingleton<AdminModelTestService>();
        services.AddTransient<Core.Providers.SsrfGuardingHttpHandler>();
        services.AddHttpClient<OpenAiCompatibleProviderModelsClient>()
            .ConfigureHttpClient(static client => client.Timeout = TimeSpan.FromSeconds(30))
            // Do not auto-follow redirects: a 3xx to an internal host would otherwise bypass the
            // host blocklist. Combined with the SSRF guard that resolves + validates the target host.
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<Core.Providers.SsrfGuardingHttpHandler>();
        return services;
    }

    public static IApplicationBuilder UseGatewayRequestId(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestIdMiddleware>();
}
