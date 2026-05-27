using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class AdminModelGrantEndpoints
{
    public static IEndpointRouteBuilder MapAdminModelGrantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api")
            .RequireAuthorization(GatewayAuthPolicies.Admin);

        group.MapGet("/tenant/model-grants", GetTenantGrantsAsync);
        group.MapPut("/tenant/model-grants", PutTenantGrantsAsync);

        group.MapGet("/keys/{id:guid}/model-grants", GetApiKeyGrantsAsync);
        group.MapPut("/keys/{id:guid}/model-grants", PutApiKeyGrantsAsync);

        return endpoints;
    }

    private static async Task<IResult> GetTenantGrantsAsync(
        HttpContext httpContext,
        IModelGrantAdminService grants,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var response = await grants.GetTenantGrantsAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return Results.Json(response);
    }

    private static async Task<IResult> PutTenantGrantsAsync(
        HttpContext httpContext,
        IModelGrantAdminService grants,
        IAuditLogger audit,
        [FromBody] ReplaceModelGrantsRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        try
        {
            var response = await grants
                .ReplaceTenantGrantsAsync(tenantId, request ?? new ReplaceModelGrantsRequest(), cancellationToken)
                .ConfigureAwait(false);
            audit.LogAdminAction(
                "tenant.model_grants.replace",
                new AuditLogEntry(
                    tenantId.ToString(),
                    httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                    new { response.ModelIds, response.UsesDefaultAccess }));

            return Results.Json(response);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> GetApiKeyGrantsAsync(
        Guid id,
        HttpContext httpContext,
        IModelGrantAdminService grants,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        try
        {
            var response = await grants.GetApiKeyGrantsAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
            return Results.Json(response);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> PutApiKeyGrantsAsync(
        Guid id,
        HttpContext httpContext,
        IModelGrantAdminService grants,
        IAuditLogger audit,
        [FromBody] ReplaceModelGrantsRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        try
        {
            var response = await grants
                .ReplaceApiKeyGrantsAsync(tenantId, id, request ?? new ReplaceModelGrantsRequest(), cancellationToken)
                .ConfigureAwait(false);
            audit.LogAdminAction(
                "api_key.model_grants.replace",
                new AuditLogEntry(
                    tenantId.ToString(),
                    httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                    new { KeyId = id, response.ModelIds, response.UsesDefaultAccess }));

            return Results.Json(response);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static bool TryGetTenantId(HttpContext context, out Guid tenantId)
    {
        tenantId = default;
        if (!context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var value) ||
            value is not TenantContext tenant)
        {
            return false;
        }

        return Guid.TryParse(tenant.TenantId, out tenantId);
    }
}
