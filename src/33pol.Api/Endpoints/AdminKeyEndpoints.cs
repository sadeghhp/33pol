using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        group.MapPost("/{id:guid}/archive", ArchiveKeyAsync);
        group.MapPost("/{id:guid}/unarchive", UnarchiveKeyAsync);
        group.MapDelete("/{id:guid}", DeleteKeyAsync);
        group.MapGet("/{id:guid}/lifecycle", GetKeyLifecycleAsync);
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

        AdminApiKeyCreatedResponse created;
        try
        {
            created = await adminKeys.CreateAsync(tenantId, request, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // Input validation (a past expiry, for instance) is the caller's mistake, not an upstream
            // failure; without this it surfaced as a 502 and an Error-level gateway error record.
            return Results.BadRequest(new { message = ex.Message });
        }

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
        bool? includeArchived,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var keys = await adminKeys
            .ListAsync(
                tenantId,
                includeUsageSummary == true,
                includeArchived == true,
                GetActorKeyId(httpContext),
                cancellationToken)
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
        catch (ArgumentException ex)
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
            await adminKeys.RevokeAsync(tenantId, id, GetActorKeyId(httpContext), cancellationToken)
                .ConfigureAwait(false);
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
        catch (ApiKeyLifecycleException ex)
        {
            return Conflict(ex);
        }
    }

    private static async Task<IResult> ArchiveKeyAsync(
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
            await adminKeys.ArchiveAsync(tenantId, id, GetActorKeyId(httpContext), cancellationToken)
                .ConfigureAwait(false);
            audit.LogAdminAction(
                "api_key.archive",
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
        catch (ApiKeyLifecycleException ex)
        {
            return Conflict(ex);
        }
    }

    private static async Task<IResult> UnarchiveKeyAsync(
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
            await adminKeys.UnarchiveAsync(tenantId, id, GetActorKeyId(httpContext), cancellationToken)
                .ConfigureAwait(false);
            audit.LogAdminAction(
                "api_key.unarchive",
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
        catch (ApiKeyLifecycleException ex)
        {
            return Conflict(ex);
        }
    }

    private static async Task<IResult> DeleteKeyAsync(
        Guid id,
        // Explicit: minimal APIs do not infer a body for DELETE, and the confirmation belongs in the
        // body rather than the query string so it stays out of access logs.
        [FromBody] DeleteAdminApiKeyRequest? request,
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
            // The service hands back a snapshot of what it removed: after the row is gone the id
            // resolves to nothing, so an audit entry carrying only the id records which credential
            // was destroyed in name alone.
            var deleted = await adminKeys
                .DeleteAsync(tenantId, id, GetActorKeyId(httpContext), request?.ConfirmKeyPrefix, cancellationToken)
                .ConfigureAwait(false);

            audit.LogAdminAction(
                "api_key.delete",
                new AuditLogEntry(
                    tenantId.ToString(),
                    httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                    new
                    {
                        KeyId = id,
                        deleted.KeyPrefix,
                        deleted.Label,
                        deleted.Assignee,
                        deleted.CostCenter,
                        deleted.Role,
                        deleted.CreatedAt,
                        deleted.RevokedAt,
                    }));

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
        catch (ApiKeyLifecycleException ex)
        {
            return Conflict(ex);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> GetKeyLifecycleAsync(
        Guid id,
        HttpContext httpContext,
        IAdminKeyService adminKeys,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        try
        {
            var lifecycle = await adminKeys.GetLifecycleAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
            return Results.Json(lifecycle);
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

        var revokedCount = await adminKeys
            .RevokeManyAsync(tenantId, keyIds, GetActorKeyId(httpContext), cancellationToken)
            .ConfigureAwait(false);
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

    /// <summary>
    /// The admin key on the request, when there is one. Recorded against every lifecycle event and
    /// used to stop a key acting on itself.
    /// </summary>
    private static Guid? GetActorKeyId(HttpContext context) =>
        Guid.TryParse(context.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value, out var id) ? id : null;

    private static IResult Conflict(ApiKeyLifecycleException ex) =>
        Results.Json(
            new ApiKeyLifecycleConflictResponse
            {
                Code = ex.Code,
                Message = ex.Message,
                BillingEventCount = ex.Code == "key_has_usage" ? ex.BillingEventCount : null,
                LastUsedAt = ex.LastUsedAt,
            },
            statusCode: StatusCodes.Status409Conflict);

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
