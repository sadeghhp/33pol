using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Security.Audit;
using Pol33.Security.Configuration;

namespace Pol33.Security.Tests.Audit;

public sealed class FileAuditLogReaderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "33pol-audit-reader-" + Guid.NewGuid().ToString("N"));

    private FileAuditLogger CreateLogger(long maxBytes = 8 * 1024 * 1024) =>
        new(
            Options.Create(new GatewaySecurityOptions
            {
                AuditLogPath = Path.Combine(_directory, "audit-log.jsonl"),
                AuditLogMaxBytes = maxBytes,
            }),
            NullLogger<FileAuditLogger>.Instance);

    [Fact]
    public async Task ReadRecent_MissingFile_IsUnavailableAndEmpty()
    {
        using var logger = CreateLogger();
        var reader = new FileAuditLogReader(logger);

        reader.IsAvailable.Should().BeFalse();
        var result = await reader.ReadRecentAsync(10);
        result.Entries.Should().BeEmpty();
        result.NewestUtc.Should().BeNull();
    }

    [Fact]
    public async Task ReadRecent_ReturnsNewestFirstWithDetailsAsJson()
    {
        using var logger = CreateLogger();
        logger.LogAdminAction("api_key.create", new AuditLogEntry("tenant-1", "key-1", new { KeyPrefix = "sk-33pol-abcd" }));
        logger.LogAdminAction("model.update", new AuditLogEntry("tenant-1", "key-1", new { Id = "m1" }));
        logger.LogAdminAction("errors.clear", new AuditLogEntry("tenant-1", null));
        var reader = new FileAuditLogReader(logger);

        reader.IsAvailable.Should().BeTrue();
        var result = await reader.ReadRecentAsync(2);

        result.Entries.Select(e => e.Action).Should().Equal("errors.clear", "model.update");
        result.Entries[1].Details.Should().Contain("\"id\":\"m1\"");
        result.Entries[0].Details.Should().BeNull();
        result.Entries[0].ApiKeyId.Should().BeNull();
        result.ParseErrors.Should().Be(0);
        result.NewestUtc.Should().Be(result.Entries[0].TimestampUtc);
    }

    [Fact]
    public async Task ReadRecent_SkipsAndCountsMalformedLines()
    {
        using var logger = CreateLogger();
        logger.LogAdminAction("a.one", new AuditLogEntry("t", null));
        File.AppendAllText(logger.AuditLogPath, "{not json\n\n{\"action\":\"missing.timestamp\"}\n");
        logger.LogAdminAction("a.two", new AuditLogEntry("t", null));
        var reader = new FileAuditLogReader(logger);

        var result = await reader.ReadRecentAsync(10);

        result.Entries.Select(e => e.Action).Should().Equal("a.two", "a.one");
        result.ParseErrors.Should().Be(2, "the blank line is ignored, the two bad records are counted");
    }

    [Fact]
    public async Task ReadRecent_SpansTheRolledGenerationWhenTheCurrentFileIsShort()
    {
        // A tiny cap forces a rotation after the first record.
        using var logger = CreateLogger(maxBytes: GatewaySecurityOptions.MinimumAuditLogBytes);
        var padding = new string('x', (int)GatewaySecurityOptions.MinimumAuditLogBytes);
        logger.LogAdminAction("old.big", new AuditLogEntry("t", null, new { Padding = padding }));
        logger.LogAdminAction("new.small", new AuditLogEntry("t", null));
        File.Exists(logger.AuditLogPath + ".1").Should().BeTrue("the oversize file rolled to .1");
        var reader = new FileAuditLogReader(logger);

        var result = await reader.ReadRecentAsync(10);

        result.Entries.Select(e => e.Action).Should().Equal("new.small", "old.big");
    }

    [Fact]
    public async Task ReadRecent_ManyRecords_ReadsAcrossChunkBoundaries()
    {
        using var logger = CreateLogger();
        for (var i = 0; i < 2000; i++)
        {
            logger.LogAdminAction("bulk." + i, new AuditLogEntry("t", null, new { Index = i, Filler = new string('f', 100) }));
        }

        var result = await new FileAuditLogReader(logger).ReadRecentAsync(200);

        result.Entries.Should().HaveCount(200);
        result.Entries[0].Action.Should().Be("bulk.1999");
        result.Entries[^1].Action.Should().Be("bulk.1800");
        result.ParseErrors.Should().Be(0);
    }

    [Fact]
    public async Task ReadRecent_WithAPrefix_ReturnsOnlyMatchingActions_AndSaysWhetherMoreExist()
    {
        using var logger = CreateLogger();
        for (var i = 0; i < 3; i++)
        {
            logger.LogAdminAction("rate_limits.update", new AuditLogEntry("t", "k", new { Version = i }));
            logger.LogAdminAction("api_key.create", new AuditLogEntry("t", "k", null));
        }

        var reader = new FileAuditLogReader(logger);
        var page = await reader.ReadRecentAsync(new AuditLogQuery(2, "rate_limits."));
        var all = await reader.ReadRecentAsync(new AuditLogQuery(10, "rate_limits."));

        page.Entries.Should().HaveCount(2).And.OnlyContain(e => e.Action == "rate_limits.update");
        page.HasMore.Should().BeTrue();
        all.Entries.Should().HaveCount(3);
        all.HasMore.Should().BeFalse();
        all.ScanLimitReached.Should().BeFalse();
    }

    /// <summary>
    /// The trail is shared with every other admin action, so a filter that matches little must not
    /// be allowed to read all of it. Stopping early is reported, because "nothing older" and "did
    /// not look further" are different answers.
    /// </summary>
    [Fact]
    public async Task ReadRecent_StopsAtTheScanCeiling_AndSaysSo()
    {
        using var logger = CreateLogger();
        logger.LogAdminAction("rate_limits.update", new AuditLogEntry("t", "k", null));
        for (var i = 0; i < 20; i++)
        {
            logger.LogAdminAction("api_key.create", new AuditLogEntry("t", "k", null));
        }

        var result = await new FileAuditLogReader(logger)
            .ReadRecentAsync(new AuditLogQuery(5, "rate_limits.", MaxScanned: 10));

        result.Entries.Should().BeEmpty();
        result.ScanLimitReached.Should().BeTrue();
    }

    /// <summary>
    /// Paging must not lose a record just because it shares a clock tick with the one before it.
    /// The trail is written from whatever <see cref="TimeProvider"/> the host has — coarse on some
    /// platforms, fixed in tests, and shared between replicas appending to one file — so equal
    /// timestamps at a page boundary are a question of when, not whether.
    /// </summary>
    [Fact]
    public async Task ReadRecent_PagingAcrossRecordsThatShareATimestamp_LosesNothing()
    {
        using var logger = new FileAuditLogger(
            Options.Create(new GatewaySecurityOptions
            {
                AuditLogPath = Path.Combine(_directory, "audit-log.jsonl"),
                AuditLogMaxBytes = 8 * 1024 * 1024,
            }),
            NullLogger<FileAuditLogger>.Instance,
            new FrozenTimeProvider(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)));

        for (var i = 0; i < 5; i++)
        {
            logger.LogAdminAction("rate_limits.update", new AuditLogEntry("t", "k", new { Version = i }));
        }

        var reader = new FileAuditLogReader(logger);
        var seen = new List<string>();
        AuditLogCursor? cursor = null;

        for (var page = 0; page < 5; page++)
        {
            var result = await reader.ReadRecentAsync(new AuditLogQuery(2, "rate_limits.", cursor));
            seen.AddRange(result.Entries.Select(e => e.Details ?? string.Empty));
            if (!result.HasMore)
            {
                break;
            }

            cursor = result.NextCursor;
            cursor.Should().NotBeNull("a page that reports more must say where to resume");
        }

        // Every record exactly once, newest first, even though all five share one timestamp.
        seen.Should().Equal(
            """{"version":4}""",
            """{"version":3}""",
            """{"version":2}""",
            """{"version":1}""",
            """{"version":0}""");
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
