using Microsoft.EntityFrameworkCore;
using Pol33.Core.Billing;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class BillingEventRepositoryTests
{
    [Fact]
    public async Task TryAppendAsync_FirstRequestId_ReturnsTrue()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(TryAppendAsync_FirstRequestId_ReturnsTrue));
        var repository = new BillingEventRepository(db);

        var appended = await repository.TryAppendAsync(CreateEvent("req-first"));

        appended.Should().BeTrue();
        (await db.BillingEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TryAppendAsync_DuplicateRequestId_ReturnsFalseAndLeavesSingleRow()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(TryAppendAsync_DuplicateRequestId_ReturnsFalseAndLeavesSingleRow));
        var repository = new BillingEventRepository(db);

        (await repository.TryAppendAsync(CreateEvent("req-dup", promptTokens: 10))).Should().BeTrue();
        (await repository.TryAppendAsync(CreateEvent("req-dup", promptTokens: 999))).Should().BeFalse();

        var events = await db.BillingEvents.AsNoTracking().ToListAsync();
        events.Should().ContainSingle();
        events[0].PromptTokens.Should().Be(10);
    }

    [Fact]
    public async Task TryAppendAsync_EmptyRequestId_Throws()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(TryAppendAsync_EmptyRequestId_Throws));
        var repository = new BillingEventRepository(db);

        var act = () => repository.TryAppendAsync(CreateEvent("   "));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static BillingEventRecord CreateEvent(string requestId, long promptTokens = 1) =>
        new(
            Guid.NewGuid(),
            requestId,
            Guid.NewGuid(),
            null,
            "gpt-4o",
            null,
            promptTokens,
            1,
            null,
            null,
            0.01m,
            50,
            DateTimeOffset.UtcNow);
}
