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
        group.MapPatch("/{id:guid}", UpdateKeyAsync);
        group.MapGet("/{id:guid}/usage", GetKeyUsageAsync);
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
                new { created.Id, created.KeyPrefix, created.Label, created.Assignee, created.CostCenter }));

        return Results.Json(created, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListKeysAsync(
        HttpContext httpContext,
        IAdminKeyService adminKeys,
        bool? includeUsageSummary,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var keys = await adminKeys
            .ListAsync(tenantId, includeUsageSummary == true, cancellationToken)
            .ConfigureAwait(false);
        return Results.Json(keys);
    }

    private static async Task<IResult> UpdateKeyAsync(
        Guid id,
        UpdateAdminApiKeyRequest request,
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
            var updated = await adminKeys.UpdateAsync(tenantId, id, request, cancellationToken).ConfigureAwait(false);
            audit.LogAdminAction(
                "api_key.update",
                new AuditLogEntry(
                    tenantId.ToString(),
                    httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                    new
                    {
                        KeyId = id,
                        request.Label,
                        request.Assignee,
                        request.Description,
                        request.CostCenter,
                    }));

            return Results.Json(updated);
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

    private static async Task<IResult> GetKeyUsageAsync(
        Guid id,
        HttpContext httpContext,
        IAdminKeyService adminKeys,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        try
        {
            var usage = await adminKeys
                .GetUsageAsync(tenantId, id, from, to, cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(usage);
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
