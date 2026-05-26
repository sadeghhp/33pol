using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Abstractions;
using Pol33.Security.Audit;

namespace Pol33.Security.Tests.Audit;

public sealed class NoOpAuditLoggerTests
{
    [Fact]
    public void LogAdminAction_DoesNotThrow()
    {
        var sut = new NoOpAuditLogger(NullLogger<NoOpAuditLogger>.Instance);
        var act = () => sut.LogAdminAction("config.reload", new AuditLogEntry("t1", "k1", new { ok = true }));
        act.Should().NotThrow();
    }
}
