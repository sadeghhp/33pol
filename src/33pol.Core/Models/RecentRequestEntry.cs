namespace Pol33.Core.Models;

/// <remarks>
/// A record rather than a class so the live feed can restamp an in-flight entry's elapsed duration
/// with <c>with</c> on every read, without mutating the shared instance other readers hold.
/// </remarks>
public sealed record RecentRequestEntry
{
    public required string RequestId { get; init; }

    public required string Method { get; init; }

    public required string Path { get; init; }

    public string? ModelId { get; init; }

    public string? TenantId { get; init; }

    public int StatusCode { get; init; }

    public double DurationMs { get; init; }

    public bool IsStreaming { get; init; }

    public string? ErrorCode { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// True while the request is still being forwarded. In-flight entries carry the elapsed time so
    /// far in <see cref="DurationMs"/> and a <see cref="StatusCode"/> of 0 — the upstream has not
    /// answered yet. They exist only in memory and are never persisted to a snapshot.
    /// </summary>
    /// <remarks>
    /// Without these the dashboard could not show an inference that was actually running: every
    /// counter and every feed row was written at completion, so a 60-second non-streaming call left
    /// the console reporting an idle gateway for its whole duration.
    /// </remarks>
    public bool IsInFlight { get; init; }

    /// <summary>
    /// The cost center this request is billed to — the API key's own when it has one, otherwise the
    /// tenant default. Known from admission, so in-flight rows carry it too.
    /// </summary>
    public string? CostCenter { get; init; }

    /// <summary>Prompt tokens as reported (or estimated) at completion; null until usage is known.</summary>
    public long? PromptTokens { get; init; }

    public long? CompletionTokens { get; init; }

    /// <summary>
    /// Prompt + completion, or the upstream's combined figure when it reported no split.
    /// </summary>
    public long? TotalTokens { get; init; }

    /// <summary>See <see cref="RecentRequestUsage.TokenSource"/>.</summary>
    public string? TokenSource { get; init; }

    public decimal? InputCost { get; init; }

    public decimal? OutputCost { get; init; }

    public decimal? TotalCost { get; init; }

    public string? Currency { get; init; }

    /// <summary>
    /// <c>pending</c> while the usage event is queued for pricing, <c>priced</c> once the billing
    /// pipeline has attached costs, <c>unpriced</c> when it never will (no rate card, or no billing
    /// store). Null when the request produced no usage at all — a rejection, a failed forward, or
    /// an upstream body the gateway could not parse usage from.
    /// </summary>
    public string? PricingStatus { get; init; }

    /// <summary>Copies the billable outcome onto this row.</summary>
    public RecentRequestEntry WithUsage(RecentRequestUsage usage) => this with
    {
        PromptTokens = usage.PromptTokens,
        CompletionTokens = usage.CompletionTokens,
        TotalTokens = usage.TotalTokens,
        TokenSource = usage.TokenSource,
        InputCost = usage.InputCost,
        OutputCost = usage.OutputCost,
        TotalCost = usage.TotalCost,
        Currency = usage.Currency,
        PricingStatus = usage.PricingStatus,
    };
}
