using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IConfigReload
{
    bool IsReloadInProgress { get; }

    Task<ConfigReloadResult> ReloadAsync(CancellationToken cancellationToken = default);

    ConfigStatusResponse GetStatus();
}
