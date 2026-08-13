namespace Pol33.Core.Models;

public enum GatewayErrorSort
{
    LastSeen = 0,
    FirstSeen = 1,
    Count = 2,
}

/// <summary>
/// Every filter the Errors tab can apply, in one shape shared by the endpoint, the in-memory store
/// and the database repository.
/// </summary>
/// <remarks>
/// <see cref="Clamp"/> lives here rather than in the endpoint so all three agree on the bounds. A
/// store that clamps differently from the endpoint reports a total the caller cannot page through.
/// </remarks>
public sealed record GatewayErrorQuery
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxExportLimit = 10_000;
    public const int MaxOffset = 10_000;
    public const int MaxSearchLength = 200;

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public GatewayLogLevel? MinimumLevel { get; init; }

    public string? ModelId { get; init; }

    public int? StatusCode { get; init; }

    public string? EventCode { get; init; }

    public string? TenantId { get; init; }

    public string? RequestId { get; init; }

    public string? Fingerprint { get; init; }

    public string? Search { get; init; }

    public GatewayErrorSort Sort { get; init; } = GatewayErrorSort.LastSeen;

    public int Limit { get; init; } = DefaultLimit;

    public int Offset { get; init; }

    /// <summary>
    /// Normalizes user input into a query every store can execute: bounded limit and offset, a
    /// trimmed search term, and a coherent time window.
    /// </summary>
    /// <param name="maxLimit">Raised to <see cref="MaxExportLimit"/> for the export endpoint.</param>
    public GatewayErrorQuery Clamp(int maxLimit = MaxLimit)
    {
        var from = From;
        var to = To;

        // An inverted window is a user slip, not a request for zero rows.
        if (from is not null && to is not null && from > to)
        {
            (from, to) = (to, from);
        }

        var search = Search?.Trim();
        if (search is { Length: 0 })
        {
            search = null;
        }
        else if (search is not null && search.Length > MaxSearchLength)
        {
            search = search[..MaxSearchLength];
        }

        return this with
        {
            From = from,
            To = to,
            Search = search,
            ModelId = NullIfBlank(ModelId),
            EventCode = NullIfBlank(EventCode),
            TenantId = NullIfBlank(TenantId),
            RequestId = NullIfBlank(RequestId),
            Fingerprint = NullIfBlank(Fingerprint),
            Limit = Math.Clamp(Limit <= 0 ? DefaultLimit : Limit, 1, maxLimit),
            Offset = Math.Clamp(Offset, 0, MaxOffset),
        };
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Maps the admin API's <c>?sort=</c> value. Unknown values fall back to newest-first.</summary>
    public static GatewayErrorSort ParseSort(string? sort) => sort?.ToLowerInvariant() switch
    {
        "firstseen" or "first_seen" => GatewayErrorSort.FirstSeen,
        "count" => GatewayErrorSort.Count,
        _ => GatewayErrorSort.LastSeen,
    };
}
