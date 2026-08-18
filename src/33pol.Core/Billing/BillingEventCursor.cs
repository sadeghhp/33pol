using System.Globalization;
using System.Text;

namespace Pol33.Core.Billing;

/// <summary>
/// Keyset cursor for paging the billing ledger newest-first.
/// </summary>
/// <remarks>
/// The ledger is ordered by <c>RecordedAt</c> descending, and several events can legitimately share
/// a timestamp, so a bare timestamp cursor would either repeat or skip rows at the boundary. The
/// cursor therefore carries the ids already served <em>at</em> the boundary timestamp; the next page
/// starts at <c>RecordedAt &lt;= At</c> and excludes exactly those ids. That is exact regardless of
/// how ids compare, and it stays small: ties at tick resolution are rare.
/// </remarks>
public sealed record BillingEventCursor(DateTimeOffset At, IReadOnlyList<Guid> BoundaryIds)
{
    public string Encode()
    {
        var builder = new StringBuilder();
        builder.Append(At.UtcTicks.ToString(CultureInfo.InvariantCulture));
        foreach (var id in BoundaryIds)
        {
            builder.Append('|').Append(id.ToString("N"));
        }

        return Convert.ToBase64String(Encoding.ASCII.GetBytes(builder.ToString()));
    }

    public static bool TryDecode(string? encoded, out BillingEventCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            var raw = Encoding.ASCII.GetString(Convert.FromBase64String(encoded.Trim()));
            var parts = raw.Split('|');
            // long.TryParse accepts values DateTimeOffset cannot represent; without the range check a
            // crafted cursor turned this "Try" method into an ArgumentOutOfRangeException (a 500
            // instead of a 400 at the admin endpoint).
            if (parts.Length == 0 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            var ids = new List<Guid>(parts.Length - 1);
            for (var i = 1; i < parts.Length; i++)
            {
                if (!Guid.TryParseExact(parts[i], "N", out var id))
                {
                    return false;
                }

                ids.Add(id);
            }

            cursor = new BillingEventCursor(new DateTimeOffset(ticks, TimeSpan.Zero), ids);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the cursor that continues after <paramref name="lastPage"/> (newest-first). When the
    /// page that produced <paramref name="previous"/> ended on the same timestamp, its boundary ids
    /// are carried forward — a run of ties can span more than one page.
    /// </summary>
    public static BillingEventCursor? After(
        IReadOnlyList<BillingEventRecord> lastPage,
        BillingEventCursor? previous = null)
    {
        if (lastPage.Count == 0)
        {
            return null;
        }

        var boundary = lastPage[^1].RecordedAt;
        var ids = lastPage.Where(e => e.RecordedAt == boundary).Select(e => e.Id).ToList();
        if (previous is not null && previous.At == boundary)
        {
            ids = previous.BoundaryIds.Concat(ids).Distinct().ToList();
        }

        return new BillingEventCursor(boundary, ids);
    }
}
