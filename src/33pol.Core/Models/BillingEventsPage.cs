using Pol33.Core.Billing;

namespace Pol33.Core.Models;

public sealed class BillingEventsPage
{
    public required IReadOnlyList<AdminBillingEventListItem> Events { get; init; }

    public required int Limit { get; init; }
}
