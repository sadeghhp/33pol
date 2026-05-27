using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Pol33.Api.Services;
using Pol33.Core.Providers;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class AdminProviderEndpoints
{
    public static IEndpointRouteBuilder MapAdminProviderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api/providers")
            .RequireAuthorization(GatewayAuthPolicies.Admin);

        group.MapGet("/catalog", ListProviders);
        group.MapGet("/{providerId}/models", ListProviderModels);
        group.MapGet("/models", ListCustomProviderModels);

        // Backward compatibility
        group.MapGet("/openrouter/models", ListOpenRouterModelsLegacy);

        return endpoints;
    }

    private static IResult ListProviders()
    {
        var providers = ProviderCatalog.ListBuiltIn()
            .Select(ToProviderListDto)
            .ToList();

        providers.Add(ToProviderListDto(
            new ProviderDefinition(
                ProviderCatalog.CustomProviderId,
                "Custom (OpenAI-compatible)",
                string.Empty,
                string.Empty,
                string.Empty,
                RequiresUpstreamAuth: true)));

        return Results.Json(new { data = providers });
    }

    private static object ToProviderListDto(ProviderDefinition p) => new
    {
        id = p.Id,
        displayName = p.DisplayName,
        upstreamBaseUrl = p.UpstreamBaseUrl,
        modelsListUrl = p.ModelsListUrl,
        defaultEnvVar = p.DefaultEnvVar,
        requiresUpstreamAuth = p.RequiresUpstreamAuth,
    };

    private static async Task<IResult> ListProviderModels(
        string providerId,
        IConfiguration configuration,
        OpenAiCompatibleProviderModelsClient client,
        string? envVar,
        CancellationToken cancellationToken)
    {
        if (string.Equals(providerId, ProviderCatalog.CustomProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                detail: "Use GET /admin/api/providers/models with modelsUrl for custom providers.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!ProviderCatalog.TryGetBuiltIn(providerId, out var definition) || definition is null)
        {
            return Results.Problem(
                detail: $"Unknown provider '{providerId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var resolvedEnvVar = string.IsNullOrWhiteSpace(envVar)
            ? definition.DefaultEnvVar
            : envVar.Trim();

        if (definition.RequiresUpstreamAuth &&
            string.IsNullOrWhiteSpace(resolvedEnvVar))
        {
            return Results.Problem(
                detail: $"Provider '{definition.DisplayName}' requires an envVar for the upstream API key.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var token = ResolveBearerToken(configuration, resolvedEnvVar);
        if (definition.RequiresUpstreamAuth && string.IsNullOrWhiteSpace(token))
        {
            return Results.Problem(
                detail: $"Missing API token. Set environment variable '{resolvedEnvVar}'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Uri.TryCreate(definition.ModelsListUrl, UriKind.Absolute, out var modelsListUri))
        {
            return Results.Problem(
                detail: "Provider models list URL is invalid.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var models = await client.ListModelsAsync(modelsListUri, token ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(new
        {
            provider = definition.Id,
            upstreamBaseUrl = definition.UpstreamBaseUrl,
            data = models,
        });
    }

    private static async Task<IResult> ListCustomProviderModels(
        IConfiguration configuration,
        OpenAiCompatibleProviderModelsClient client,
        string? modelsUrl,
        string? envVar,
        CancellationToken cancellationToken)
    {
        if (!ProviderModelsListUrlValidator.TryValidate(modelsUrl, out var modelsListUri, out var validationError))
        {
            return Results.Problem(
                detail: validationError,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(envVar))
        {
            return Results.Problem(
                detail: "envVar is required for custom provider model discovery.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var token = ResolveBearerToken(configuration, envVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.Problem(
                detail: $"Missing API token. Set environment variable '{envVar.Trim()}'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var models = await client.ListModelsAsync(modelsListUri!, token, cancellationToken).ConfigureAwait(false);

        var upstreamBaseUrl = DeriveUpstreamBaseUrl(modelsListUri!);

        return Results.Json(new
        {
            provider = ProviderCatalog.CustomProviderId,
            upstreamBaseUrl,
            data = models,
        });
    }

    private static Task<IResult> ListOpenRouterModelsLegacy(
        IConfiguration configuration,
        OpenAiCompatibleProviderModelsClient client,
        string? envVar,
        CancellationToken cancellationToken) =>
        ListProviderModels("openrouter", configuration, client, envVar, cancellationToken);

    private static string? ResolveBearerToken(IConfiguration configuration, string envVar)
    {
        if (string.IsNullOrWhiteSpace(envVar))
        {
            return null;
        }

        return configuration[envVar.Trim()]
            ?? Environment.GetEnvironmentVariable(envVar.Trim());
    }

    private static string DeriveUpstreamBaseUrl(Uri modelsListUri)
    {
        var path = modelsListUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/v1/models".Length];
        }
        else if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/models".Length];
        }

        var builder = new UriBuilder(modelsListUri.Scheme, modelsListUri.Host, modelsListUri.Port, path);
        return builder.Uri.ToString().TrimEnd('/');
    }
}
