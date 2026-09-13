using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pol33.Core.RateLimiting;

/// <summary>
/// How a rule's windows are stored: one JSON document per rule, camel-cased, ISO-8601 instants.
/// </summary>
/// <remarks>
/// A column of JSON rather than a child table because the whole rule set is replaced wholesale on
/// every admin write and loaded whole on every snapshot refresh; there is no query that wants a
/// window without its rule. The document stays readable in a database browser, and an entry a
/// newer build wrote with fields this one does not know is kept rather than dropped.
/// </remarks>
public static class RateLimitScheduleJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Null for an empty schedule, so a rule without windows stores nothing.</summary>
    public static string? Serialize(IReadOnlyList<RateLimitWindowDefinition>? windows) =>
        windows is null || windows.Count == 0 ? null : JsonSerializer.Serialize(windows, Options);

    /// <summary>
    /// Parses a stored schedule. Returns false for a document that cannot be read, so the caller
    /// can log it and apply the base tier rather than fail the whole configuration load.
    /// </summary>
    public static bool TryDeserialize(string? json, out IReadOnlyList<RateLimitWindowDefinition> windows)
    {
        windows = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<RateLimitWindowDefinition>>(json, Options);
            windows = parsed?.Where(static w => w is not null).ToArray() ?? [];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
