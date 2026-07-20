using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Api.Contracts;

public sealed class AdminModelListItem
{
    public required ModelConfig Model { get; init; }

    public bool HasUpstreamCredential { get; init; }

    /// <summary>Current per-million-token price, or null when the model is unpriced.</summary>
    public ModelPricing? Pricing { get; init; }
}
