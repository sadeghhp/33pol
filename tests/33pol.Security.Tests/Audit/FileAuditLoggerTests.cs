using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Security.Audit;
using Pol33.Security.Configuration;

namespace Pol33.Security.Tests.Audit;

public sealed class FileAuditLoggerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "33pol-audit-" + Guid.NewGuid().ToString("N"));

    private FileAuditLogger Create(long maxBytes = 8 * 1024 * 1024, string fileName = "audit-log.jsonl") =>
        new(
            Options.Create(new GatewaySecurityOptions
            {
                AuditLogPath = Path.Combine(_directory, fileName),
                AuditLogMaxBytes = maxBytes,
            }),
            NullLogger<FileAuditLogger>.Instance);

    [Fact]
    public void LogAdminAction_WritesOneJsonLinePerAction()
    {
        using var sut = Create();

        sut.LogAdminAction("api_key.create", new AuditLogEntry("tenant-1", "key-1", new { KeyPrefix = "sk-33pol-abcd" }));
        sut.LogAdminAction("api_key.revoke", new AuditLogEntry("tenant-1", "key-1", new { KeyId = "key-2" }));

        var lines = File.ReadAllLines(sut.AuditLogPath);
        lines.Should().HaveCount(2);

        using var first = JsonDocument.Parse(lines[0]);
        first.RootElement.GetProperty("action").GetString().Should().Be("api_key.create");
        first.RootElement.GetProperty("tenantId").GetString().Should().Be("tenant-1");
        first.RootElement.GetProperty("apiKeyId").GetString().Should().Be("key-1");
        first.RootElement.GetProperty("details").GetProperty("keyPrefix").GetString().Should().Be("sk-33pol-abcd");
        first.RootElement.GetProperty("timestampUtc").GetDateTimeOffset()
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        using var second = JsonDocument.Parse(lines[1]);
        second.RootElement.GetProperty("action").GetString().Should().Be("api_key.revoke");
    }

    [Fact]
    public void LogAdminAction_CreatesTheFileOwnerReadWriteOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var sut = Create();

        sut.LogAdminAction("config.reload", new AuditLogEntry(null, null, new { Status = "ok" }));

        File.GetUnixFileMode(sut.AuditLogPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void LogAdminAction_CreatesTheDirectoryWhenMissing()
    {
        using var sut = Create(fileName: Path.Combine("nested", "audit-log.jsonl"));

        sut.LogAdminAction("config.reload", new AuditLogEntry(null, null, new { Status = "ok" }));

        File.Exists(sut.AuditLogPath).Should().BeTrue();
    }

    /// <summary>
    /// A record is written after the mutation it describes has already been applied, so a failure to
    /// write must not surface as an error for a change that succeeded.
    /// </summary>
    [Fact]
    public void LogAdminAction_WhenThePathIsUnwritable_DoesNotThrow()
    {
        Directory.CreateDirectory(_directory);
        // The parent of the log file is a *file*, so every write attempt fails.
        var blocker = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocker, "not a directory");

        using var sut = new FileAuditLogger(
            Options.Create(new GatewaySecurityOptions
            {
                AuditLogPath = Path.Combine(blocker, "audit-log.jsonl"),
            }),
            NullLogger<FileAuditLogger>.Instance);

        var act = () => sut.LogAdminAction("cors.update", new AuditLogEntry("t", "k", new { OriginCount = 1 }));

        act.Should().NotThrow();
    }

    [Fact]
    public void LogAdminAction_AtTheSizeCap_RollsToOneGenerationOfHistory()
    {
        // Below the floor, so the effective cap is MinimumAuditLogBytes.
        using var sut = Create(maxBytes: 1);
        var bulky = new string('x', 4096);

        for (var i = 0; i < 40; i++)
        {
            sut.LogAdminAction("rate_limits.update", new AuditLogEntry("t", "k", new { Note = bulky }));
        }

        File.Exists(sut.AuditLogPath).Should().BeTrue();
        File.Exists(sut.AuditLogPath + ".1").Should().BeTrue("the previous generation is kept");
        Directory.GetFiles(_directory).Should().HaveCount(2, "history is bounded at one generation");
        new FileInfo(sut.AuditLogPath).Length
            .Should().BeLessThan(GatewaySecurityOptions.MinimumAuditLogBytes + bulky.Length * 2);
    }

    [Fact]
    public void MaxBytes_BelowTheFloor_IsRaisedToIt()
    {
        using var sut = Create(maxBytes: 1);

        // One record is far below the floor, so nothing rolls yet.
        sut.LogAdminAction("maintenance.backup", new AuditLogEntry("t", "k", new { Succeeded = true }));

        File.Exists(sut.AuditLogPath + ".1").Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
