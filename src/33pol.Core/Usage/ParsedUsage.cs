namespace Pol33.Core.Usage;

/// <summary>
/// How much is actually known about a response's token usage.
/// </summary>
public enum UsageParseKind
{
    /// <summary>No usage could be read: absent, malformed, or unusable values.</summary>
    None = 0,

    /// <summary>Input and output tokens were reported separately and can be priced at their own rates.</summary>
    Split = 1,

    /// <summary>
    /// Only a combined total was reported. The input/output split — which is what pricing depends on
    /// — is unknown.
    /// </summary>
    TotalOnly = 2,
}

/// <summary>
/// The outcome of reading a <c>usage</c> object from an upstream response.
/// </summary>
/// <remarks>
/// Keeping <see cref="UsageParseKind.TotalOnly"/> distinct from <see cref="UsageParseKind.Split"/>
/// is a billing-correctness requirement. The previous parser folded a lone <c>total_tokens</c> into
/// the prompt-token field, so the whole total was priced at the input rate — typically several times
/// cheaper than output — and every model reporting only a total was silently under-billed with no
/// signal that anything had been assumed.
/// </remarks>
public readonly record struct ParsedUsage(UsageParseKind Kind, long PromptTokens, long CompletionTokens, long TotalTokens)
{
    public static readonly ParsedUsage None = new(UsageParseKind.None, 0, 0, 0);

    public static ParsedUsage Split(long promptTokens, long completionTokens) =>
        new(UsageParseKind.Split, promptTokens, completionTokens, promptTokens + completionTokens);

    public static ParsedUsage TotalOnly(long totalTokens) =>
        new(UsageParseKind.TotalOnly, 0, 0, totalTokens);

    public bool HasUsage => Kind != UsageParseKind.None;

    /// <summary>Total tokens however they were reported, for quota accounting that does not price them.</summary>
    public long BillableTokenTotal => Kind == UsageParseKind.Split
        ? PromptTokens + CompletionTokens
        : TotalTokens;
}
