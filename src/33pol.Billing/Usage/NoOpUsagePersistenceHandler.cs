using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public sealed class NoOpUsagePersistenceHandler : IUsagePersistenceHandler
{
    public ValueTask PersistAsync(UsageEvent usageEvent, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
