using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
            services.AddSingleton<IModelGrantAdminService, NullModelGrantAdminService>();
            services.AddSingleton<IAdminKeyService, NullAdminKeyService>();
            services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
            return services;
        }

        services.AddMemoryCache();
        services.AddSingleton<IValidateOptions<GatewaySecurityOptions>, GatewaySecurityOptionsValidator>();
        services
            .AddOptions<GatewaySecurityOptions>()
            .Bind(configuration.GetSection(GatewaySecurityOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
        services.AddScoped<IApiKeyValidator, ApiKeyValidator>();
        services.AddScoped<IModelGrantService, ModelGrantService>();
        services.AddScoped<IModelGrantAdminService, ModelGrantAdminService>();
        services.AddScoped<IAdminKeyService, AdminKeyService>();
        services.AddScoped<IApiKeyLastUsedTracker, DebouncedApiKeyLastUsedTracker>();
        services.AddHostedService<GatewayAuthenticationInitializer>();

        return services;
    }

    public static IApplicationBuilder UseGatewaySecurity(this IApplicationBuilder app, IConfiguration configuration)
    {
        // Required for endpoint RequireAuthorization even when the database is disabled (handler allows all).
        app.UseAuthentication();
        app.UseAuthorization();

        var connectionString = configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            app.UseMiddleware<Middleware.GatewayAuthorizationMiddleware>();
        }

        return app;
    }
}

public sealed class GatewaySecurityOptionsValidator : IValidateOptions<GatewaySecurityOptions>
{
    private readonly IHostEnvironment _environment;

    public GatewaySecurityOptionsValidator(IHostEnvironment environment) => _environment = environment;

    public ValidateOptionsResult Validate(string? name, GatewaySecurityOptions options)
    {
        // The key pepper is only a development convenience default. Outside Development it protects
        // every stored API-key hash, so refuse to start with an empty, default, or too-short value
        // rather than silently hashing keys with a publicly-known secret.
        if (_environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        var pepper = options.KeyPepper?.Trim();
        if (string.IsNullOrEmpty(pepper)
            || Pol33.Core.Security.WellKnownWeakSecrets.IsWeakPepper(pepper)
            || pepper.Length < GatewaySecurityOptions.MinimumPepperLength)
        {
            return ValidateOptionsResult.Fail(
                $"{GatewaySecurityOptions.SectionName}:KeyPepper must be set to a strong, non-default "
                + $"value of at least {GatewaySecurityOptions.MinimumPepperLength} characters outside Development. "
                + "Set the GATEWAY_KEY_PEPPER environment variable to a freshly generated secret.");
        }

        return ValidateOptionsResult.Success;
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
    public Task<bool> IsModelAllowedAsync(
        Guid tenantId,
        Guid apiKeyId,
        string canonicalModelId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public void InvalidateTenantGrants(Guid tenantId)
    {
    }

    public void InvalidateApiKeyGrants(Guid apiKeyId)
    {
    }
}

internal sealed class NullModelGrantAdminService : IModelGrantAdminService
{
    private static InvalidOperationException NotConfigured() =>
        new("Model grant administration requires ConnectionStrings:GatewayDb.");

    public Task<ModelGrantsResponse> GetTenantGrantsAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<ModelGrantsResponse> ReplaceTenantGrantsAsync(
        Guid tenantId,
        ReplaceModelGrantsRequest request,
        CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<ModelGrantsResponse> GetApiKeyGrantsAsync(
        Guid tenantId,
        Guid apiKeyId,
        CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<ModelGrantsResponse> ReplaceApiKeyGrantsAsync(
        Guid tenantId,
        Guid apiKeyId,
        ReplaceModelGrantsRequest request,
        CancellationToken cancellationToken = default) =>
        throw NotConfigured();
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
        bool includeUsageSummary = false,
        CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<AdminApiKeyListItem> UpdateAsync(
        Guid tenantId,
        Guid keyId,
        UpdateAdminApiKeyRequest request,
        CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<AdminApiKeyUsageResponse> GetUsageAsync(
        Guid tenantId,
        Guid keyId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task RevokeAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<int> RevokeManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> keyIds,
        CancellationToken cancellationToken = default) =>
        throw NotConfigured();
}
