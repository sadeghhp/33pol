using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
            .RequireAuthorization(GatewayAuthPolicies.Operator);

        group.MapGet("/catalog", ListProviders);
        // Provider model discovery is POST-only so env var names never appear in URLs or access logs.
        group.MapPost("/{providerId}/models", PostProviderModels);
        group.MapGet("/{providerId}/models", ProviderModelsGetNotAllowed);
        group.MapPost("/models", PostCustomProviderModels);
        group.MapGet("/models", ProviderModelsGetNotAllowed);
        group.MapPost("/openrouter/models", PostOpenRouterModelsLegacy);
        group.MapGet("/openrouter/models", ProviderModelsGetNotAllowed);

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
                RequiresUpstreamAuth: false)));

        return Results.Json(new { data = providers });
    }

    private static IResult ProviderModelsGetNotAllowed() =>
        Results.Problem(
            detail: "Provider model discovery requires POST with a JSON body. Do not pass envVar or API keys in query strings.",
            statusCode: StatusCodes.Status405MethodNotAllowed);

    private static object ToProviderListDto(ProviderDefinition p) => new
    {
        id = p.Id,
        displayName = p.DisplayName,
        upstreamBaseUrl = p.UpstreamBaseUrl,
        modelsListUrl = p.ModelsListUrl,
        defaultEnvVar = p.DefaultEnvVar,
        requiresUpstreamAuth = p.RequiresUpstreamAuth,
    };

    private static Task<IResult> PostProviderModels(
        string providerId,
        [FromBody] ProviderModelsDiscoveryRequest? body,
        IConfiguration configuration,
        UpstreamEnvVarPolicy envVarPolicy,
        OpenAiCompatibleProviderModelsClient client,
        CancellationToken cancellationToken) =>
        DiscoverProviderModelsAsync(providerId, configuration, envVarPolicy, client, body?.EnvVar, cancellationToken);

    private static async Task<IResult> DiscoverProviderModelsAsync(
        string providerId,
        IConfiguration configuration,
        UpstreamEnvVarPolicy envVarPolicy,
        OpenAiCompatibleProviderModelsClient client,
        string? envVar,
        CancellationToken cancellationToken)
    {
        if (string.Equals(providerId, ProviderCatalog.CustomProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                detail: "Use POST /admin/api/providers/models with modelsUrl for custom providers.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!ProviderCatalog.TryGetBuiltIn(providerId, out var definition) || definition is null)
        {
            return Results.Problem(
                detail: $"Unknown provider '{providerId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        string? resolvedEnvVar;
        if (string.IsNullOrWhiteSpace(envVar))
        {
            resolvedEnvVar = definition.DefaultEnvVar;
        }
        else if (!EnvVarNameValidator.TryValidate(envVar, out var normalized, out var validationError))
        {
            return Results.Problem(detail: validationError, statusCode: StatusCodes.Status400BadRequest);
        }
        else
        {
            resolvedEnvVar = normalized;
        }

        if (definition.RequiresUpstreamAuth && string.IsNullOrWhiteSpace(resolvedEnvVar))
        {
            return Results.Problem(
                detail: $"Provider '{definition.DisplayName}' requires an envVar for the upstream API key.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.IsNullOrWhiteSpace(resolvedEnvVar) &&
            !envVarPolicy.IsAllowed(resolvedEnvVar, out var policyError))
        {
            return Results.Problem(detail: policyError, statusCode: StatusCodes.Status400BadRequest);
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

        return await ListModelsOrUpstreamProblemAsync(
            () => client.ListModelsAsync(modelsListUri, token ?? string.Empty, cancellationToken),
            models => Results.Json(new
            {
                provider = definition.Id,
                upstreamBaseUrl = definition.UpstreamBaseUrl,
                data = models,
            })).ConfigureAwait(false);
    }

    private static Task<IResult> PostCustomProviderModels(
        [FromBody] CustomProviderModelsDiscoveryRequest? body,
        IConfiguration configuration,
        UpstreamEnvVarPolicy envVarPolicy,
        OpenAiCompatibleProviderModelsClient client,
        CancellationToken cancellationToken) =>
        DiscoverCustomProviderModelsAsync(
            configuration,
            envVarPolicy,
            client,
            body?.ModelsUrl,
            body?.EnvVar,
            cancellationToken);

    private static async Task<IResult> DiscoverCustomProviderModelsAsync(
        IConfiguration configuration,
        UpstreamEnvVarPolicy envVarPolicy,
        OpenAiCompatibleProviderModelsClient client,
        string? modelsUrl,
        string? envVar,
        CancellationToken cancellationToken)
    {
        if (!ProviderModelsListUrlValidator.TryValidate(modelsUrl, out var modelsListUri, out var urlError))
        {
            return Results.Problem(detail: urlError, statusCode: StatusCodes.Status400BadRequest);
        }

        string? bearerToken = null;
        if (!string.IsNullOrWhiteSpace(envVar))
        {
            if (!EnvVarNameValidator.TryValidate(envVar, out var resolvedEnvVar, out var envError))
            {
                return Results.Problem(detail: envError, statusCode: StatusCodes.Status400BadRequest);
            }

            // The caller chose both the variable to read and the URL to send it to, so without this
            // check the endpoint would forward any secret in the gateway's environment to any host.
            if (!envVarPolicy.IsAllowed(resolvedEnvVar, out var policyError))
            {
                return Results.Problem(detail: policyError, statusCode: StatusCodes.Status400BadRequest);
            }

            bearerToken = ResolveBearerToken(configuration, resolvedEnvVar);
            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                return Results.Problem(
                    detail: $"Missing API token. Set environment variable '{resolvedEnvVar}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var upstreamBaseUrl = DeriveUpstreamBaseUrl(modelsListUri!);

        return await ListModelsOrUpstreamProblemAsync(
            () => client.ListModelsAsync(modelsListUri!, bearerToken ?? string.Empty, cancellationToken),
            models => Results.Json(new
            {
                provider = ProviderCatalog.CustomProviderId,
                upstreamBaseUrl,
                data = models,
            })).ConfigureAwait(false);
    }

    private static async Task<IResult> ListModelsOrUpstreamProblemAsync(
        Func<Task<IReadOnlyList<ProviderModelListItem>>> listModels,
        Func<IReadOnlyList<ProviderModelListItem>, IResult> onSuccess)
    {
        try
        {
            var models = await listModels().ConfigureAwait(false);
            return onSuccess(models);
        }
        catch (ProviderModelsUpstreamException ex)
        {
            return Results.Problem(
                title: "Upstream model list failed",
                detail: $"The provider returned HTTP {(int)ex.StatusCode} ({ex.StatusCode}).",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static Task<IResult> PostOpenRouterModelsLegacy(
        [FromBody] ProviderModelsDiscoveryRequest? body,
        IConfiguration configuration,
        UpstreamEnvVarPolicy envVarPolicy,
        OpenAiCompatibleProviderModelsClient client,
        CancellationToken cancellationToken) =>
        DiscoverProviderModelsAsync("openrouter", configuration, envVarPolicy, client, body?.EnvVar, cancellationToken);

    private static string? ResolveBearerToken(IConfiguration configuration, string? envVar)
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

    public sealed record ProviderModelsDiscoveryRequest(string? EnvVar);

    public sealed record CustomProviderModelsDiscoveryRequest(string? ModelsUrl, string? EnvVar);
}
