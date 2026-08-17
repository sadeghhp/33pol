namespace Pol33.Core.Models;

/// <summary>
/// The billable outcome of a request, attached to its live-feed row once the billing pipeline has
/// priced it. Token counts arrive with the response; the cost side is filled in later — pricing
/// happens in the asynchronous usage writer, one flush interval after the request finished.
/// </summary>
/// <param name="TokenSource">
/// How the counts were obtained: <c>split</c> (authoritative), <c>totalOnly</c> (no
/// input/output split, priced at the dearer rate) or <c>estimated</c> (client disconnected before
/// the usage frame; approximated from frames streamed).
/// </param>
/// <param name="PricingStatus">
/// <c>priced</c> when a rate card was applied, <c>unpriced</c> when the model has none (or no
/// billing store is configured), <c>pending</c> while the usage event is queued for pricing.
/// </param>
public sealed record RecentRequestUsage(
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    string TokenSource,
    decimal? InputCost,
    decimal? OutputCost,
    decimal? TotalCost,
    string? Currency,
    string PricingStatus)
{
    public const string StatusPending = "pending";
    public const string StatusPriced = "priced";
    public const string StatusUnpriced = "unpriced";
}
