namespace Pol33.Registry.Hosting;

/// <summary>
/// Result of the last stored-credential verification, kept so the health endpoint can surface a
/// pepper mismatch instead of leaving it as a single log line at startup.
/// </summary>
public sealed class UpstreamSecretVerificationState
{
    private volatile Snapshot? _result;

    public int Total => _result?.Total ?? 0;

    public int Undecryptable => _result?.Undecryptable ?? 0;

    /// <summary>True once verification has run, whether or not it found a problem.</summary>
    public bool HasRun => _result is not null;

    public void Record(int total, int undecryptable) => _result = new Snapshot(total, undecryptable);

    private sealed record Snapshot(int Total, int Undecryptable);
}
