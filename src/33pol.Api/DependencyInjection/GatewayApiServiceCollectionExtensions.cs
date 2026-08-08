using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        // TryAdd: the registry module registers this too, because the bearer-token resolver enforces
        // the policy at the point of use. Whichever module is composed first wins; both need it.
        services.TryAddSingleton(sp => Core.Providers.UpstreamEnvVarPolicy.FromOptions(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Configuration.GatewayOptions>>().Value));
        services.AddSingleton<AdminModelProvisioningService>();
        services.AddSingleton<AdminModelTestService>();
        services.AddTransient<Core.Providers.SsrfGuardingHttpHandler>();
        services.AddHttpClient<OpenAiCompatibleProviderModelsClient>()
            .ConfigureHttpClient(static client => client.Timeout = TimeSpan.FromSeconds(30))
            // Do not auto-follow redirects: a 3xx to an internal host would otherwise bypass the
            // host blocklist. The primary handler additionally validates the address each connection
            // is actually opened to, which the resolve-then-send guard alone cannot.
            .ConfigurePrimaryHttpMessageHandler(Core.Providers.SsrfGuardingHttpHandler.CreateGuardedPrimaryHandler)
            .AddHttpMessageHandler<Core.Providers.SsrfGuardingHttpHandler>();

        // The admin "test model" probe gets its own client rather than borrowing the inference one,
        // so redirects can be refused: the probe reports the upstream's status back to the caller,
        // and following a 3xx would let a redirect steer that oracle at a host the operator never
        // configured.
        //
        // It deliberately does NOT carry the address blocklist that guards provider discovery. That
        // blocklist refuses private and loopback addresses, which is right for a URL typed into a
        // discovery form, but local upstreams — LM Studio, Ollama, vLLM on 127.0.0.1 or a private
        // subnet — are a primary supported deployment here, and this probe only ever targets a URL
        // already configured as a model's upstream. An admin who can set that URL can already send
        // inference to it, so blocking the probe would break the documented local-upstream workflow
        // without removing any capability the caller does not already have.
        services.AddHttpClient(AdminModelTestService.HttpClientName)
            .ConfigureHttpClient(static client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false });

        return services;
    }

    public static IApplicationBuilder UseGatewayRequestId(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestIdMiddleware>();
}
