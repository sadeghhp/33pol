using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Identity;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

/// <summary>
/// Repositories clamp any single ledger query to <see cref="UsageExportLimits.MaxEventPageSize"/>,
/// which equals the export cap, so probing with "cap + 1" in one query could never see the extra
/// row and an over-cap export silently reported itself complete. The exporter must page with the
/// keyset cursor and probe past the cap.
/// </summary>
public sealed class BillingUsageServiceExportTruncationTests
{
    [Fact]
    public async Task ExportEventsAsync_ExactlyMaxRows_IsNotTruncated()
    {
        var (service, repo) = CreateService(UsageExportLimits.MaxEventRows);

        var result = await service.ExportEventsAsync(new BillingEventQuery(), "csv");

        result.Truncated.Should().BeFalse();
        CountRows(result.Body).Should().Be(UsageExportLimits.MaxEventRows);
        repo.LargestLimitRequested.Should().BeLessThanOrEqualTo(UsageExportLimits.MaxEventPageSize);
    }

    [Fact]
    public async Task ExportEventsAsync_MaxRowsPlusOne_FlagsTruncationAndExportsExactlyMaxRows()
    {
        var (service, _) = CreateService(UsageExportLimits.MaxEventRows + 1);

        var result = await service.ExportEventsAsync(new BillingEventQuery(), "csv");

        result.Truncated.Should().BeTrue();
        CountRows(result.Body).Should().Be(UsageExportLimits.MaxEventRows);
    }

    [Fact]
    public async Task ExportEventsAsync_FewerThanMaxRows_ReturnsAllWithoutProbing()
    {
        var (service, repo) = CreateService(7);

        var result = await service.ExportEventsAsync(new BillingEventQuery(), "json");

        result.Truncated.Should().BeFalse();
        repo.QueryCount.Should().Be(1, "a short page proves the ledger is exhausted");
    }

    /// <summary>
    /// Ties at the boundary timestamp must be neither repeated nor skipped when paging past them.
    /// </summary>
    [Fact]
    public async Task ExportEventsAsync_PagesThroughTiedTimestamps_WithoutDuplicatesOrGaps()
    {
        var (service, _) = CreateService(UsageExportLimits.MaxEventRows + 3, tiedTimestamps: true);

        var result = await service.ExportEventsAsync(new BillingEventQuery(), "csv");

        result.Truncated.Should().BeTrue();
        var requestIds = result.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => line.Split(',')[1])
            .ToList();
        requestIds.Should().HaveCount(UsageExportLimits.MaxEventRows);
        requestIds.Should().OnlyHaveUniqueItems();
    }

    private static int CountRows(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1; // minus header

    private static (BillingUsageService Service, FakeLedger Ledger) CreateService(int rowCount, bool tiedTimestamps = false)
    {
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = new List<BillingEventRecord>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            // Tied: 100 events per tick, so page boundaries fall inside a run of equal timestamps.
            var at = tiedTimestamps ? start.AddTicks(i / 100) : start.AddTicks(i);
            rows.Add(new BillingEventRecord(
                Guid.NewGuid(), $"req-{i}", null, null, "gpt-4o", null, 1, 1, null, null, null, 1, at));
        }

        var ledger = new FakeLedger(rows);
        var apiKeys = Substitute.For<IApiKeyRepository>();
        apiKeys.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApiKeyRecord>());
        var service = new BillingUsageService(
            Substitute.For<IDailyUsageRollupRepository>(),
            ledger,
            apiKeys,
            Substitute.For<IRateCardRepository>(),
            Options.Create(new BillingOptions()),
            Substitute.For<IModelRegistry>());
        return (service, ledger);
    }

    /// <summary>Mirrors the persistence repository's keyset paging and page clamp.</summary>
    private sealed class FakeLedger(IReadOnlyList<BillingEventRecord> rows) : IBillingEventRepository
    {
        public int QueryCount { get; private set; }

        public int LargestLimitRequested { get; private set; }

        public Task<IReadOnlyList<BillingEventRecord>> QueryAsync(
            BillingEventQuery query,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            LargestLimitRequested = Math.Max(LargestLimitRequested, query.Limit);
            var limit = Math.Clamp(query.Limit, 1, UsageExportLimits.MaxEventPageSize);

            IEnumerable<BillingEventRecord> ordered = rows
                .OrderByDescending(r => r.RecordedAt)
                .ThenByDescending(r => r.Id);
            if (query.Cursor is not null)
            {
                var at = query.Cursor.At;
                var excluded = new HashSet<Guid>(query.Cursor.BoundaryIds);
                ordered = ordered.Where(r => r.RecordedAt <= at && !excluded.Contains(r.Id));
            }

            return Task.FromResult<IReadOnlyList<BillingEventRecord>>(ordered.Take(limit).ToList());
        }

        public Task<bool> TryAppendAsync(BillingEventRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, ApiKeyUsageSummary>> GetUsageSummariesAsync(
            Guid tenantId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DailyUsageRollupRecord>> GetDailyTotalsAsync(
            DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DailyUsageRollupRecord>> AggregateDailyAsync(
            BillingEventQuery filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
