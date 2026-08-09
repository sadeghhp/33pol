using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IUsagePersistenceHandler
{
    ValueTask PersistAsync(UsageEvent usageEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes out anything <see cref="PersistAsync"/> has accepted but not yet durably stored.
    /// </summary>
    /// <remarks>
    /// The usage recorder calls this after draining its queue at shutdown. A handler that batches
    /// must persist here even if its own hosted lifecycle has already stopped: hosted services stop
    /// in reverse registration order, so the batch handler's StopAsync can run <em>before</em> the
    /// recorder hands it the final events — without this call, whatever the drain delivered short of
    /// a full batch sat in the buffer with no flush loop left to write it, and was lost at process
    /// exit on every deploy under traffic.
    /// </remarks>
    ValueTask FlushPendingAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
