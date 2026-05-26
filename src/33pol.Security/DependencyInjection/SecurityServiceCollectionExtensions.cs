using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.Security;
using Pol33.Persistence.DependencyInjection;
using Pol33.Security.Authentication;
using Pol33.Security.Authorization;
using Pol33.Security.Configuration;
using Pol33.Security.Hosting;
using Pol33.Security.Audit;
using Pol33.Security.Services;

namespace Pol33.Security.DependencyInjection;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddGatewaySecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName);
        services.AddSingleton<GatewayAuthenticationState>();
        services.AddSingleton<IGatewayAuthenticationState>(sp => sp.GetRequiredService<GatewayAuthenticationState>());

        services.AddSingleton<IAuthorizationHandler, GatewayAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(GatewayAuthPolicies.Inference, policy =>
                policy.AddRequirements(new GatewayAuthorizationRequirement(GatewayAuthPolicies.Inference)));

            options.AddPolicy(GatewayAuthPolicies.Admin, policy =>
                policy.AddRequirements(new GatewayAuthorizationRequirement(GatewayAuthPolicies.Admin)));
        });

        services
            .AddAuthentication(GatewayAuthSchemes.ApiKey)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                GatewayAuthSchemes.ApiKey,
                _ => { });

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<IApiKeyValidator, NullApiKeyValidator>();
            services.AddSingleton<IModelGrantService, NullModelGrantService>();
            services.AddSingleton<IAdminKeyService, NullAdminKeyService>();
            services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
            return services;
        }

        services.AddMemoryCache();
        services
            .AddOptions<GatewaySecurityOptions>()
            .Bind(configuration.GetSection(GatewaySecurityOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
        services.AddScoped<IApiKeyValidator, ApiKeyValidator>();
        services.AddScoped<IModelGrantService, ModelGrantService>();
        services.AddScoped<IAdminKeyService, AdminKeyService>();
        services.AddHostedService<GatewayAuthenticationInitializer>();

        return services;
    }

    public static IApplicationBuilder UseGatewaySecurity(this IApplicationBuilder app, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return app;
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<Middleware.GatewayAuthorizationMiddleware>();
        return app;
    }
}

internal sealed class NullApiKeyValidator : IApiKeyValidator
{
    public Task<ApiKeyValidationResult> ValidateAsync(string? apiKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Missing));

    public void InvalidateCache(Guid apiKeyId)
    {
    }
}

internal sealed class NullModelGrantService : IModelGrantService
{
    public Task<bool> IsModelAllowedAsync(Guid tenantId, string canonicalModelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

internal sealed class NullAdminKeyService : IAdminKeyService
{
    private static InvalidOperationException NotConfigured() =>
        new("API key administration requires ConnectionStrings:GatewayDb.");

    public Task<AdminApiKeyCreatedResponse> CreateAsync(
        Guid tenantId,
        CreateAdminApiKeyRequest request,
        CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<IReadOnlyList<AdminApiKeyListItem>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task RevokeAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken = default) =>
        throw NotConfigured();
}
