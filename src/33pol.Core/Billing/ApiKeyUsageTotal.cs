namespace Pol33.Core.Billing;

/// <summary>Ledger totals for one API key; <see cref="ApiKeyId"/> is <see langword="null"/> for requests sent without a key.</summary>
public sealed record ApiKeyUsageTotal(
    Guid? ApiKeyId,
    int RequestCount,
    long PromptTokens,
    long CompletionTokens,
    decimal TotalCost);
