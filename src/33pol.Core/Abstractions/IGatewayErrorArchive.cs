using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Durable storage for error records. Implemented over the database and resolved per-scope, so
/// nothing on the request path ever holds a DbContext.
/// </summary>
public interface IGatewayErrorArchive
{
    Task AppendBatchAsync(IReadOnlyList<GatewayErrorRecord> batch, CancellationToken cancellationToken = default);

    Task<GatewayErrorPage> QueryAsync(GatewayErrorQuery query, CancellationToken cancellationToken = default);

    Task<GatewayErrorGroupPage> QueryGroupsAsync(GatewayErrorQuery query, CancellationToken cancellationToken = default);

    Task<GatewayErrorRecord?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<GatewayErrorFacets> GetFacetsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken = default);

    Task<int> DeleteAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops records older than <paramref name="olderThan"/>, then trims the oldest surviving rows
    /// down to <paramref name="maxRows"/>. Returns the total removed.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, int maxRows, CancellationToken cancellationToken = default);
}

/// <summary>
/// Buffers records off the request path and writes them to the archive in batches.
/// </summary>
public interface IGatewayErrorArchiveWriter
{
    void Enqueue(GatewayErrorRecord record);

    /// <summary>Writes everything buffered. Reads call this so a query cannot miss a just-recorded error.</summary>
    Task FlushPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops everything buffered without writing it. Used by clear-all so records captured before
    /// the wipe cannot land in the database after it.
    /// </summary>
    void DiscardPending();
}
