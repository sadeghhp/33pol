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

    public Task<AuditLogReadResult> ReadRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        ReadRecentAsync(new AuditLogQuery(limit), cancellationToken);

    public Task<AuditLogReadResult> ReadRecentAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var take = Math.Clamp(query.Limit, 1, MaxLimit);
        var maxScanned = Math.Max(take, query.MaxScanned);
        var entries = new List<AuditLogEntryView>(take);
        var parseErrors = 0;
        var scanned = 0;
        var hasMore = false;
        var scanLimitReached = false;

        // Records sharing the cursor's timestamp that this page has already handed out. They are
        // contiguous in the walk, so skipping the first `Skip` of them resumes exactly where the
        // previous page stopped — without losing the rest of the group or repeating it.
        var skippedAtCursor = 0;

        foreach (var path in new[] { logger.AuditLogPath, logger.AuditLogPath + ".1" })
        {
            if (hasMore || scanLimitReached)
            {
                break;
            }

            foreach (var line in ReadLinesBackwards(path, cancellationToken))
            {
                if (scanned >= maxScanned)
                {
                    scanLimitReached = true;
                    break;
                }

                scanned++;

                if (!TryParse(line, out var entry))
                {
                    parseErrors++;
                    continue;
                }

                if (query.ActionPrefix is { Length: > 0 } prefix &&
                    !entry.Action.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (query.Cursor is { } cursor)
                {
                    if (entry.TimestampUtc > cursor.TimestampUtc)
                    {
                        continue;
                    }

                    if (entry.TimestampUtc == cursor.TimestampUtc && skippedAtCursor < cursor.Skip)
                    {
                        skippedAtCursor++;
                        continue;
                    }
                }

                // One past the page is read only to learn that it exists.
                if (entries.Count >= take)
                {
                    hasMore = true;
                    break;
                }

                entries.Add(entry);
            }
        }

        return Task.FromResult(
            new AuditLogReadResult(entries, parseErrors, entries.Count > 0 ? entries[0].TimestampUtc : null)
            {
                HasMore = hasMore,
                ScanLimitReached = scanLimitReached,
                NextCursor = hasMore && entries.Count > 0 ? NextCursor(query, entries) : null,
            });
    }

    /// <summary>Where this page ended, counting the boundary group it may have split.</summary>
    private static AuditLogCursor NextCursor(AuditLogQuery query, List<AuditLogEntryView> entries)
    {
        var last = entries[^1].TimestampUtc;
        var inThisPage = entries.Count(entry => entry.TimestampUtc == last);

        // A page that neither started nor ended the group carries the earlier page's count forward.
        var alreadySkipped = query.Cursor is { } cursor && cursor.TimestampUtc == last ? cursor.Skip : 0;
        return new AuditLogCursor(last, inThisPage + alreadySkipped);
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
