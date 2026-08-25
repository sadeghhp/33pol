namespace Pol33.Core.Abstractions;

/// <summary>
/// Durable key/value store for small operational facts (the last backup result, for one). Values
/// are serialised as JSON; keys are dotted names such as <c>backup.last</c>.
/// </summary>
public interface IMaintenanceStateStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class;
}

public static class MaintenanceStateKeys
{
    public const string LastBackup = "backup.last";
}
