using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IUsagePersistenceHandler
{
    ValueTask PersistAsync(UsageEvent usageEvent, CancellationToken cancellationToken = default);
}
