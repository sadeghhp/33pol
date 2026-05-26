using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;

namespace Pol33.Security.Audit;

public sealed class NoOpAuditLogger(ILogger<NoOpAuditLogger> logger) : IAuditLogger
{
    public void LogAdminAction(string action, AuditLogEntry entry) =>
        logger.LogInformation(
            "Audit {Action} tenant={TenantId} apiKey={ApiKeyId} details={@Details}",
            action,
            entry.TenantId,
            entry.ApiKeyId,
            entry.Details);
}
