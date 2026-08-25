using System.Text;
using System.Text.Json;
using Pol33.Core.Abstractions;

namespace Pol33.Security.Audit;

/// <summary>
/// Tails the JSON-lines audit trail that <see cref="FileAuditLogger"/> writes: reads the current
/// file backwards in chunks until it has enough records, then continues into the single rolled
/// generation (<c>.1</c>) if the current file is short. Malformed lines are skipped and counted
/// rather than failing the read — a torn last line during rotation is expected, not an error.
/// </summary>
public sealed class FileAuditLogReader(FileAuditLogger logger) : IAuditLogReader
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxLimit = 200;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => File.Exists(logger.AuditLogPath) || File.Exists(logger.AuditLogPath + ".1");

    public Task<AuditLogReadResult> ReadRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, MaxLimit);
        var entries = new List<AuditLogEntryView>(take);
        var parseErrors = 0;

        foreach (var path in new[] { logger.AuditLogPath, logger.AuditLogPath + ".1" })
        {
            if (entries.Count >= take)
            {
                break;
            }

            foreach (var line in ReadLinesBackwards(path, cancellationToken))
            {
                if (entries.Count >= take)
                {
                    break;
                }

                if (TryParse(line, out var entry))
                {
                    entries.Add(entry);
                }
                else
                {
                    parseErrors++;
                }
            }
        }

        return Task.FromResult(new AuditLogReadResult(entries, parseErrors, entries.Count > 0 ? entries[0].TimestampUtc : null));
    }

    private static bool TryParse(string line, out AuditLogEntryView entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("timestampUtc", out var ts) ||
                !root.TryGetProperty("action", out var action) ||
                action.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? details = null;
            if (root.TryGetProperty("details", out var d) && d.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                details = d.GetRawText();
            }

            entry = new AuditLogEntryView(
                ts.GetDateTimeOffset(),
                action.GetString()!,
                root.TryGetProperty("tenantId", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                root.TryGetProperty("apiKeyId", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null,
                details);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Yields complete lines newest-first without loading the whole file.</summary>
    private static IEnumerable<string> ReadLinesBackwards(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var position = stream.Length;
        var carry = new List<byte>();
        var buffer = new byte[ChunkSize];

        while (position > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(ChunkSize, position);
            position -= read;
            stream.Seek(position, SeekOrigin.Begin);
            var got = 0;
            while (got < read)
            {
                var n = stream.Read(buffer, got, read - got);
                if (n <= 0)
                {
                    break;
                }

                got += n;
            }

            // Prepend this chunk to whatever partial line was carried from the previous (later) chunk.
            var combined = new byte[got + carry.Count];
            Array.Copy(buffer, 0, combined, 0, got);
            carry.CopyTo(combined, got);

            var end = combined.Length;
            for (var i = combined.Length - 1; i >= 0; i--)
            {
                if (combined[i] == (byte)'\n')
                {
                    if (end > i + 1)
                    {
                        yield return Encoding.UTF8.GetString(combined, i + 1, end - i - 1).TrimEnd('\r');
                    }

                    end = i;
                }
            }

            carry = [.. combined[..end]];
        }

        if (carry.Count > 0)
        {
            yield return Encoding.UTF8.GetString(carry.ToArray()).TrimEnd('\r');
        }
    }
}
