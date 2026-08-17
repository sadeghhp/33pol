using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Core.Usage;

/// <summary>Builds the live-feed usage projection from the billing pipeline's own records.</summary>
public static class RecentRequestUsageMapper
{
    /// <summary>Token counts only, as known at completion; costs are not yet available.</summary>
    public static RecentRequestUsage FromUsageEvent(UsageEvent usageEvent, string pricingStatus)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);
        var totalOnly = usageEvent.TokenSource == UsageTokenSource.TotalOnly;
        return new RecentRequestUsage(
            usageEvent.PromptTokens,
            usageEvent.CompletionTokens,
            totalOnly ? usageEvent.TotalTokens : usageEvent.PromptTokens + usageEvent.CompletionTokens,
            TokenSourceName(usageEvent.TokenSource),
            InputCost: null,
            OutputCost: null,
            TotalCost: null,
            Currency: null,
            pricingStatus);
    }

    /// <summary>
    /// The priced outcome. A record whose costs are null was written without a rate card and is
    /// reported <c>unpriced</c> rather than pending forever.
    /// </summary>
    public static RecentRequestUsage FromBillingEvent(
        BillingEventRecord record,
        UsageEvent source,
        string? currency)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(source);
        var totalOnly = source.TokenSource == UsageTokenSource.TotalOnly;
        var priced = record.TotalCost is not null;
        return new RecentRequestUsage(
            record.PromptTokens,
            record.CompletionTokens,
            totalOnly ? source.TotalTokens : record.PromptTokens + record.CompletionTokens,
            TokenSourceName(source.TokenSource),
            record.InputCost,
            record.OutputCost,
            record.TotalCost,
            priced ? currency : null,
            priced ? RecentRequestUsage.StatusPriced : RecentRequestUsage.StatusUnpriced);
    }

    public static string TokenSourceName(UsageTokenSource source) => source switch
    {
        UsageTokenSource.TotalOnly => "totalOnly",
        UsageTokenSource.Estimated => "estimated",
        _ => "split",
    };
}
