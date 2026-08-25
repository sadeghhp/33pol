using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pol33.Core.Models;
using Pol33.Core.Models.Overview;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class MaintenanceAdminEndpoints
{
    public static IEndpointRouteBuilder MapMaintenanceAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/admin/api/maintenance/backup", CreateBackupAsync)
            .RequireAuthorization(GatewayAuthPolicies.Operator);
        return endpoints;
    }

    private static async Task<IResult> CreateBackupAsync(
        HttpContext httpContext,
        ISqliteBackupService backupService,
        IAuditLogger audit,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var result = await backupService.CreateBackupAsync(cancellationToken).ConfigureAwait(false);

        // Remembered so the Overview can say when the last backup was and whether it verified;
        // best-effort, and only where a database exists to remember it in.
        var state = httpContext.RequestServices.GetService<IMaintenanceStateStore>();
        if (state is not null)
        {
            try
            {
                await state.SetAsync(MaintenanceStateKeys.LastBackup, new BackupStatus
                {
                    AttemptedAtUtc = attemptedAt,
                    Succeeded = result.Succeeded,
                    Path = result.Path,
                    SizeBytes = result.SizeBytes,
                    IntegrityCheck = result.IntegrityCheck,
                    Error = result.Error,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The backup itself is the deliverable; failing to note it must not fail the
                // request — but it must not vanish either, or the Overview keeps saying "no
                // backup has been taken" with nothing to explain why.
                httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(MaintenanceAdminEndpoints))
                    .LogError(ex, "Backup succeeded but its result could not be recorded in maintenance state.");
            }
        }

        audit.LogAdminAction(
            "maintenance.backup",
            new AuditLogEntry(
                httpContext.User.FindFirst(GatewayAuthClaims.TenantId)?.Value,
                httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                new { result.Succeeded, result.Path, result.SizeBytes, result.IntegrityCheck }));

        // 200 on a verified backup, 503 when no database is configured, 500 on a produced-but-corrupt copy.
        var statusCode = result switch
        {
            { Succeeded: true } => StatusCodes.Status200OK,
            { Path: null } => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Results.Json(result, statusCode: statusCode);
    }
}
