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

    /// <summary>Canonical model type (see <c>ModelTypes</c>), or null when the operator left it unset.</summary>
    public string? ModelType { get; set; }

    public bool PublicAccess { get; set; }

    /// <summary>
    /// Whether the route is in service (<c>ModelRouteStates</c>). Rows written before this column
    /// existed are backfilled to <c>serving</c> by the migration, which is what they were.
    /// </summary>
    public string State { get; set; } = "serving";

    /// <summary>Serialized <c>UpstreamAuthConfig</c> (JSON), or null when the model has no upstream auth.</summary>
    public string? UpstreamAuthJson { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
