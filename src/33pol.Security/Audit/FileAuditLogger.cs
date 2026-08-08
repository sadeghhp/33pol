using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Security.Configuration;

namespace Pol33.Security.Audit;

/// <summary>
/// Durable, append-only record of every admin mutation, written as JSON Lines.
/// </summary>
/// <remarks>
/// The control plane mints and revokes API keys, rewrites model grants, CORS origins and rate
/// limits, and triggers config reloads and database backups. Those actions were previously recorded
/// only as an <c>ILogger</c> Information line, so whether a trail survived at all depended on the
/// deployed Serilog configuration — which ships with a console sink and nothing else. Nor could the
/// trail be reviewed from the console: the admin Logs tab keeps warnings and errors, in memory, for
/// 500 entries.
///
/// One line per action keeps the file greppable and append-safe: a crash mid-write costs at most the
/// last record rather than corrupting the file, and no reader has to parse the whole thing. The
/// <c>ILogger</c> line is still emitted, so existing sinks keep whatever they collect today.
///
/// Details are recorded as the caller passed them. Call sites are responsible for never putting a
/// secret in <see cref="AuditLogEntry.Details"/> — they pass key <em>ids</em> and prefixes, model
/// ids and counts, which is what makes this file safe to retain.
/// </remarks>
public sealed class FileAuditLogger : IAuditLogger, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Pinned, not inherited from a default: these field names are the trail's schema, and call
        // sites pass details as anonymous objects whose members are written PascalCase in some places
        // and camelCase in others. Normalizing here means a query written today keeps working.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // One record per line: a newline inside a record would break the format.
        WriteIndented = false,
    };

    private readonly ILogger<FileAuditLogger> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _sync = new();

    /// <summary>
    /// Last write failure reported, so a permanently unwritable path (a read-only <c>config/</c>
    /// mount) costs one warning rather than one per admin action.
    /// </summary>
    private string? _lastFailure;

    private bool _disposed;

    public FileAuditLogger(
        IOptions<GatewaySecurityOptions> options,
        ILogger<FileAuditLogger> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var configured = options.Value.AuditLogPath;
        _path = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
        _maxBytes = Math.Max(GatewaySecurityOptions.MinimumAuditLogBytes, options.Value.AuditLogMaxBytes);
    }

    /// <summary>Absolute path the trail is written to. Exposed for diagnostics and tests.</summary>
    public string AuditLogPath => _path;

    public void LogAdminAction(string action, AuditLogEntry entry)
    {
        // Unchanged from the previous behaviour, so any sink already collecting these keeps working.
        _logger.LogInformation(
            "Audit {Action} tenant={TenantId} apiKey={ApiKeyId} details={@Details}",
            action,
            entry?.TenantId,
            entry?.ApiKeyId,
            entry?.Details);

        if (entry is null)
        {
            return;
        }

        // A failure to record must never fail the admin action that produced it — the mutation has
        // already been applied by the time the call site audits it, so throwing here would report a
        // completed change as an error.
        try
        {
            Append(action, entry);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void Append(string action, AuditLogEntry entry)
    {
        var line = JsonSerializer.Serialize(
            new AuditRecord(
                _timeProvider.GetUtcNow(),
                action,
                entry.TenantId,
                entry.ApiKeyId,
                entry.Details),
            SerializerOptions);

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            RollIfOversizeLocked();

            var isNew = !File.Exists(_path);

            // ReadWrite sharing, and one open-append-close per record: replicas sharing a volume (and
            // parallel test hosts sharing a bin directory) then append instead of losing records to a
            // sharing violation. O_APPEND makes each single small write atomic, so lines never interleave.
            using var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);

            if (isNew && !OperatingSystem.IsWindows())
            {
                // Key ids and tenant ids, not secrets — but the trail names who did what, so it is
                // not world-readable.
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);

            _lastFailure = null;
        }
    }

    /// <summary>
    /// Keeps the trail bounded with a single generation of history: at the cap the current file
    /// becomes <c>.1</c> and a fresh one starts. Unbounded growth would eventually fill the same
    /// volume the registry and the database live on, which is a worse outcome than losing the
    /// oldest records.
    /// </summary>
    private void RollIfOversizeLocked()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < _maxBytes)
        {
            return;
        }

        var previous = _path + ".1";
        if (File.Exists(previous))
        {
            File.Delete(previous);
        }

        File.Move(_path, previous);
    }

    private void ReportFailure(Exception ex)
    {
        var message = ex.Message;
        lock (_sync)
        {
            if (string.Equals(_lastFailure, message, StringComparison.Ordinal))
            {
                return;
            }

            _lastFailure = message;
        }

        _logger.LogWarning(
            ex,
            "Admin audit trail could not be written to {AuditLogPath}. Admin actions are still logged "
            + "to the configured log providers, but the durable trail is incomplete until this is fixed.",
            _path);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private sealed record AuditRecord(
        DateTimeOffset TimestampUtc,
        string Action,
        string? TenantId,
        string? ApiKeyId,
        object? Details);
}
