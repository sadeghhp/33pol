namespace Pol33.Core.Models;

/// <summary>One selectable filter value and how many errors carry it.</summary>
public sealed record GatewayErrorFacetValue(string Value, long Count);

/// <summary>
/// The filter values actually present in the queried window. The console populates its dropdowns
/// from this rather than from the model registry, so it can never offer a filter that matches
/// nothing.
/// </summary>
public sealed record GatewayErrorFacets
{
    public const int MaxValuesPerFacet = 50;

    public IReadOnlyList<GatewayErrorFacetValue> Models { get; init; } = [];

    public IReadOnlyList<GatewayErrorFacetValue> Codes { get; init; } = [];

    public IReadOnlyList<GatewayErrorFacetValue> Statuses { get; init; } = [];

    public IReadOnlyList<GatewayErrorFacetValue> Levels { get; init; } = [];
}
