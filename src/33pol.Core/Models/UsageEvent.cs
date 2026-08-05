namespace Pol33.Core.Models;

/// <summary>
/// How the token counts on a <see cref="UsageEvent"/> were obtained, which determines how they may
/// be priced.
/// </summary>
public enum UsageTokenSource
{
    /// <summary>Upstream reported input and output separately; each is priced at its own rate.</summary>
    Split = 0,

    /// <summary>
    /// Upstream reported only a combined total, carried in <see cref="UsageEvent.TotalTokens"/>. The
    /// split is unknown, so pricing must apply a deliberate policy rather than assume the input rate.
    /// </summary>
    TotalOnly = 1,

    /// <summary>
    /// No authoritative usage arrived — typically the client disconnected before the terminal usage
    /// frame — so the counts are approximated from the number of response frames actually streamed.
    /// </summary>
    /// <remarks>
    /// Recording nothing in this situation meant a client that disconnects just before completion
    /// received free inference, while the upstream had already generated (and charged for) the
    /// tokens. Kept distinct from authoritative usage so it can be reconciled or excluded.
    /// </remarks>
    Estimated = 2,
}

public sealed class UsageEvent
{
    public required string RequestId { get; init; }

    public string? TenantId { get; init; }

    public string? ApiKeyId { get; init; }

    public required string ModelId { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    /// <summary>
    /// Combined token count when <see cref="TokenSource"/> is
    /// <see cref="UsageTokenSource.TotalOnly"/>; otherwise 0 (the split fields are authoritative).
    /// </summary>
    public long TotalTokens { get; init; }

    /// <summary>
    /// Defaults to <see cref="UsageTokenSource.Split"/> so existing constructions keep their
    /// meaning; only the parser sets <see cref="UsageTokenSource.TotalOnly"/>.
    /// </summary>
    public UsageTokenSource TokenSource { get; init; } = UsageTokenSource.Split;

    public double DurationMs { get; init; }

    public string? CostCenter { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }
}
