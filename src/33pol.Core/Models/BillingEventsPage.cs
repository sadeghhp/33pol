using Pol33.Core.Billing;

namespace Pol33.Core.Models;

public sealed class BillingEventsPage
{
    public required IReadOnlyList<AdminBillingEventListItem> Events { get; init; }

    public required int Limit { get; init; }

    /// <summary>True when more events exist beyond this page.</summary>
    public bool HasMore { get; init; }

    /// <summary>Opaque cursor for the next page; <see langword="null"/> when <see cref="HasMore"/> is false.</summary>
    public string? NextCursor { get; init; }
}
