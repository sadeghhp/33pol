namespace Pol33.Core.Models;

public sealed class UsageReportRequest
{
    public DateOnly? FromDate { get; init; }

    public DateOnly? ToDate { get; init; }

    public Guid? TenantId { get; init; }
}
