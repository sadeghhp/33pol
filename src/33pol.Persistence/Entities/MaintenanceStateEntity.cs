namespace Pol33.Persistence.Entities;

/// <summary>
/// Small key/value store for operational facts that have no other home — the last backup result,
/// for one — so the admin Overview can report them across restarts.
/// </summary>
public sealed class MaintenanceStateEntity
{
    public required string Key { get; set; }

    public required string ValueJson { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
