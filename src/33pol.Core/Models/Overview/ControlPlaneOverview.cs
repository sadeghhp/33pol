namespace Pol33.Core.Models.Overview;

/// <summary>Cheap process facts, refreshed with every summary.</summary>
public sealed record ControlPlaneLiveOverview
{
    public long WorkingSetBytes { get; init; }

    public long GcHeapBytes { get; init; }

    public long GcCommittedBytes { get; init; }

    public int Gen2Collections { get; init; }

    public double GcPauseTimePercent { get; init; }

    public long ThreadPoolPendingWorkItems { get; init; }

    public int ThreadPoolThreads { get; init; }

    public int ProcessorCount { get; init; }

    public double? CpuPercent { get; init; }

    public DateTimeOffset? ConfigLastReloadUtc { get; init; }

    public bool ConfigHotReloadEnabled { get; init; }

    public int ModelCount { get; init; }
}

/// <summary>Slower control-plane facts served by <c>/admin/api/overview/control-plane</c>.</summary>
public sealed record ControlPlaneOverview
{
    public DateTimeOffset BuiltAtUtc { get; init; }

    public SecretsVerificationStatus Secrets { get; init; } = new();

    public BackupStatus? LastBackup { get; init; }

    public int BackupCount { get; init; }

    public DatabaseStatus Database { get; init; } = new();

    public DateTimeOffset? AuditLastEntryUtc { get; init; }

    public DateTimeOffset? ConfigLastReloadUtc { get; init; }

    public int ModelCount { get; init; }
}

public sealed record SecretsVerificationStatus
{
    public bool HasRun { get; init; }

    public int Total { get; init; }

    public int Undecryptable { get; init; }

    public DateTimeOffset? CheckedAtUtc { get; init; }
}

public sealed record BackupStatus
{
    public DateTimeOffset AttemptedAtUtc { get; init; }

    public bool Succeeded { get; init; }

    public string? Path { get; init; }

    public long SizeBytes { get; init; }

    public string IntegrityCheck { get; init; } = string.Empty;

    public string? Error { get; init; }
}

public sealed record DatabaseStatus
{
    public bool Configured { get; init; }

    public string? Path { get; init; }

    public long? SizeBytes { get; init; }

    public string? JournalMode { get; init; }
}
