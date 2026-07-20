using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Api.Contracts;

public sealed class AdminModelWriteRequest
{
    public ModelConfig Model { get; set; } = new();

    /// <summary>Write-only upstream API key. Never returned on GET.</summary>
    public string? ApiKey { get; set; }

    /// <summary>When true on PATCH, removes stored upstream credential and clears upstreamAuth.</summary>
    public bool ClearApiKey { get; set; }

    /// <summary>
    /// Per-model token pricing. Persisted as a rate card rather than on the model itself, so it
    /// stays out of the models.json fallback registry. Null leaves existing pricing unchanged.
    /// </summary>
    public ModelPricing? Pricing { get; set; }

    /// <summary>When true, removes the model's pricing so its usage records as unpriced.</summary>
    public bool ClearPricing { get; set; }
}
