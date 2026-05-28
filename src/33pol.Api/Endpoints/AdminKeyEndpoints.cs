using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class AdminKeyEndpoints
{
    public static IEndpointRouteBuilder MapAdminKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api/keys")
            .RequireAuthorization(GatewayAuthPolicies.Admin);

        group.MapPost("/", CreateKeyAsync);
        group.MapGet("/", ListKeysAsync);
        group.MapPost("/revoke", RevokeKeysAsync);
        group.MapPost("/{id:guid}/revoke", RevokeKeyAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateKeyAsync(
        CreateAdminApiKeyRequest request,
        HttpContext httpContext,
        IAdminKeyService adminKeys,
        IAuditLogger audit,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var created = await adminKeys.CreateAsync(tenantId, request, cancellationToken).ConfigureAwait(false);
        audit.LogAdminAction(
            "api_key.create",
            new AuditLogEntry(
                tenantId.ToString(),
                httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                new { created.Id, created.KeyPrefix }));

        return Results.Json(created, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListKeysAsync(
        HttpContext httpContext,
        IAdminKeyService adminKeys,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var keys = await adminKeys.ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return Results.Json(keys);
    }

    private static async Task<IResult> RevokeKeyAsync(
        Guid id,
        HttpContext httpContext,
        IAdminKeyService adminKeys,
        IAuditLogger audit,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        try
        {
            await adminKeys.RevokeAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
            audit.LogAdminAction(
                "api_key.revoke",
                new AuditLogEntry(
                    tenantId.ToString(),
                    httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                    new { KeyId = id }));

            return Results.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
    }

    private static async Task<IResult> RevokeKeysAsync(
        BatchRevokeAdminApiKeysRequest request,
        HttpContext httpContext,
        IAdminKeyService adminKeys,
        IAuditLogger audit,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var keyIds = request.KeyIds
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        if (keyIds.Length == 0)
        {
            return Results.BadRequest(new { message = "At least one key id is required." });
        }

        var revokedCount = await adminKeys.RevokeManyAsync(tenantId, keyIds, cancellationToken).ConfigureAwait(false);
        audit.LogAdminAction(
            "api_key.revoke_batch",
            new AuditLogEntry(
                tenantId.ToString(),
                httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                new { RevokedCount = revokedCount, KeyIds = keyIds }));

        return Results.Json(
            new BatchRevokeAdminApiKeysResponse
            {
                RevokedCount = revokedCount,
            });
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
