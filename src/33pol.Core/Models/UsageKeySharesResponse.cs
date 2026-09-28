namespace Pol33.Core.Models;

/// <summary>
/// How much of the filtered load each API key carried: one row per key (plus one for requests sent
/// without a key), with its share of requests, tokens and cost in the same filter.
/// </summary>
/// <remarks>
/// Aggregated from the billing ledger: the daily rollup table has no per-key dimension. Typically
/// read with a model filter to answer "who is driving this model's traffic".
/// </remarks>
public sealed class UsageKeySharesResponse
{
    /// <summary>The model filter the shares were computed over; <see langword="null"/> is every model.</summary>
    public string? ModelId { get; init; }

    public string Currency { get; init; } = "USD";

    public int TotalRequests { get; init; }

    public long TotalTokens { get; init; }

    public decimal TotalCost { get; init; }

    /// <summary>Largest share of requests first.</summary>
    public required IReadOnlyList<UsageKeyShare> Keys { get; init; }
}

public sealed class UsageKeyShare
{
    /// <summary><see langword="null"/> for requests sent without an API key (anonymous public-model traffic).</summary>
    public Guid? ApiKeyId { get; init; }

    public string? KeyPrefix { get; init; }

    public string? Label { get; init; }

    public string? Assignee { get; init; }

    public int Requests { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public decimal TotalCost { get; init; }

    /// <summary>Fraction (0–1) of <see cref="UsageKeySharesResponse.TotalRequests"/>.</summary>
    public double RequestShare { get; init; }

    /// <summary>Fraction (0–1) of <see cref="UsageKeySharesResponse.TotalTokens"/>; 0 when no tokens were recorded.</summary>
    public double TokenShare { get; init; }

    /// <summary>Fraction (0–1) of <see cref="UsageKeySharesResponse.TotalCost"/>; <see langword="null"/> when nothing was priced.</summary>
    public double? CostShare { get; init; }
}
