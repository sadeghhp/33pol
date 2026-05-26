namespace Pol33.Core.Abstractions;

public interface IAuditLogger
{
    void LogAdminAction(string action, AuditLogEntry entry);
}

public sealed record AuditLogEntry(
    string? TenantId,
    string? ApiKeyId,
    object? Details = null);
