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

    /// <summary>
    /// The key this request's quota was checked under — the tenant id for authenticated traffic,
    /// the per-address anonymous partition (<c>anon:&lt;ip&gt;</c>) for keyless traffic.
    /// </summary>
    /// <remarks>
    /// Usage must be committed back to the same partition the admission check reads, or the check
    /// never sees it. Anonymous usage used to commit under a literal <c>"anonymous"</c> key while
    /// the check read the per-address partition, so keyless callers of public models were never
    /// held to the monthly token quota at all.
    /// </remarks>
    public string? QuotaPartition { get; init; }

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
