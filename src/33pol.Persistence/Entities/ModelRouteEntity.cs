namespace Pol33.Persistence.Entities;

/// <summary>
/// A model routing entry — the database form of a models.json model. Aliases/Capabilities are
/// stored as JSON TEXT (primitive collections); the upstream-auth config is stored as a JSON string.
/// </summary>
public sealed class ModelRouteEntity
{
    public Guid Id { get; set; }

    public required string ModelId { get; set; }

    public required string Url { get; set; }

    public int MaxContextLength { get; set; }

    public List<string> Aliases { get; set; } = [];

    public List<string> Capabilities { get; set; } = [];

    public bool PublicAccess { get; set; }

    /// <summary>Serialized <c>UpstreamAuthConfig</c> (JSON), or null when the model has no upstream auth.</summary>
    public string? UpstreamAuthJson { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
