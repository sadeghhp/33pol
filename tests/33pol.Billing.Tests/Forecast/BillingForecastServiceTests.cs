using Microsoft.Extensions.Options;
using Pol33.Billing.Forecast;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Forecast;

public sealed class BillingForecastServiceTests
{
    // 2026-05-20 is a Wednesday in a 31-day month: 19 days done (incl. today), 11 remaining.
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 5, 20);
    private static readonly Guid Tenant = Guid.NewGuid();

    private readonly IDailyUsageRollupRepository _rollups = Substitute.For<IDailyUsageRollupRepository>();
    private readonly IBillingEventRepository _events = Substitute.For<IBillingEventRepository>();
    private readonly BillingForecastService _service;

    public BillingForecastServiceTests()
    {
        _service = new BillingForecastService(
            _rollups,
            _events,
            Options.Create(new BillingOptions { DefaultCurrency = "EUR" }),
            new FixedTimeProvider(Now));
    }

    private static DailyUsageRollupRecord Row(DateOnly day, decimal cost, string? costCenter = null, Guid? tenant = null) =>
        new(day, tenant ?? Tenant, "gpt-4o", costCenter, 10, 5, cost, 1);

    private static UsageForecastRequest Request(int trailingDays = 7, string? costCenter = null, bool anonymous = false) =>
        new() { Scope = new UsageScope(Tenant, anonymous), TrailingDays = trailingDays, CostCenter = costCenter };

    [Fact]
    public async Task GetForecastAsync_ProjectsMonthToDatePlusTrailingAverageForRemainingDays()
    {
        // 7 complete days (13th..19th) at 1.00/day, plus 5.00 earlier in the month and 0.40 today.
        var records = Enumerable.Range(0, 7).Select(i => Row(Today.AddDays(-1 - i), 1.00m))
            .Append(Row(new DateOnly(2026, 5, 2), 5.00m))
            .Append(Row(Today, 0.40m))
            .ToList();
        _rollups.GetScopedRollupsAsync(Arg.Any<UsageScope>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(records);

        var forecast = await _service.GetForecastAsync(Request());

        forecast.TrailingDays.Should().Be(7);
        forecast.WindowStart.Should().Be(new DateOnly(2026, 5, 13));
        forecast.WindowEnd.Should().Be(new DateOnly(2026, 5, 19));
        forecast.TrailingTotalCost.Should().Be(7.00m);
        forecast.AverageDailyCost.Should().Be(1.00m);
        forecast.MonthToDateCost.Should().Be(12.40m);
        forecast.DaysRemainingInMonth.Should().Be(11);
        forecast.ProjectedMonthlyCost.Should().Be(12.40m + 11m);
        forecast.Currency.Should().Be("EUR");
    }

    [Fact]
    public async Task GetForecastAsync_ExcludesTodayFromTrailingAverage()
    {
        // Only today's partial spend exists: it must count toward MTD but not dilute the average.
        _rollups.GetScopedRollupsAsync(Arg.Any<UsageScope>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Row(Today, 3.00m) });

        var forecast = await _service.GetForecastAsync(Request());

        forecast.TrailingTotalCost.Should().Be(0m);
        forecast.AverageDailyCost.Should().Be(0m);
        forecast.MonthToDateCost.Should().Be(3.00m);
        forecast.ProjectedMonthlyCost.Should().Be(3.00m);
    }

    [Fact]
    public async Task GetForecastAsync_ReadsOneWindowCoveringBothTrailingAndMonthToDate()
    {
        _rollups.GetScopedRollupsAsync(Arg.Any<UsageScope>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        await _service.GetForecastAsync(Request(trailingDays: 30));

        // 30 complete days back from the 20th starts on 20 April, earlier than the 1st of May.
        await _rollups.Received(1).GetScopedRollupsAsync(
            Arg.Is<UsageScope>(s => s.TenantId == Tenant && !s.IncludeAnonymous),
            new DateOnly(2026, 4, 20),
            Today,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetForecastAsync_NoRollups_ReturnsZeroProjection()
    {
        _rollups.GetScopedRollupsAsync(Arg.Any<UsageScope>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var forecast = await _service.GetForecastAsync(Request());

        forecast.TrailingTotalCost.Should().Be(0m);
        forecast.ProjectedMonthlyCost.Should().Be(0m);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(200, 90)]
    public async Task GetForecastAsync_ClampsTrailingDays(int requestedDays, int expectedDays)
    {
        _rollups.GetScopedRollupsAsync(Arg.Any<UsageScope>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var forecast = await _service.GetForecastAsync(Request(trailingDays: requestedDays));

        forecast.TrailingDays.Should().Be(expectedDays);
    }

    [Fact]
    public async Task GetForecastAsync_FiltersByCostCenterCaseInsensitively()
    {
        _rollups.GetScopedRollupsAsync(Arg.Any<UsageScope>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Row(Today.AddDays(-1), 2.00m, "Engineering"),
                Row(Today.AddDays(-1), 9.00m, "ops"),
            });

        var forecast = await _service.GetForecastAsync(Request(costCenter: "engineering"));

        forecast.TrailingTotalCost.Should().Be(2.00m);
        forecast.MonthToDateCost.Should().Be(2.00m);
    }

    [Fact]
    public async Task GetForecastAsync_PassesAnonymousScopeThrough()
    {
        _rollups.GetScopedRollupsAsync(Arg.Any<UsageScope>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Row(Today.AddDays(-1), 1.00m, tenant: null) });

        await _service.GetForecastAsync(Request(anonymous: true));

        await _rollups.Received(1).GetScopedRollupsAsync(
            Arg.Is<UsageScope>(s => s.TenantId == Tenant && s.IncludeAnonymous),
            Arg.Any<DateOnly?>(),
            Arg.Any<DateOnly?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetForecastAsync_WithApiKey_AggregatesFromLedger()
    {
        var keyId = Guid.NewGuid();
        _events.AggregateDailyAsync(
                Arg.Is<BillingEventQuery>(q => q.ApiKeyId == keyId && q.TenantId == Tenant && q.ToDate == Today),
                Arg.Any<CancellationToken>())
            .Returns(new[] { Row(Today.AddDays(-1), 4.00m) });

        var forecast = await _service.GetForecastAsync(
            new UsageForecastRequest { Scope = new UsageScope(Tenant), ApiKeyId = keyId, TrailingDays = 1 });

        forecast.TrailingTotalCost.Should().Be(4.00m);
        forecast.ProjectedMonthlyCost.Should().Be(4.00m + 4.00m * 11);
        await _rollups.DidNotReceiveWithAnyArgs().GetScopedRollupsAsync(default!, default, default, default);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
