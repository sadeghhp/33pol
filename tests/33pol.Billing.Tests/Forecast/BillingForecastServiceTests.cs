using Microsoft.Extensions.Options;
using Pol33.Billing.Forecast;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Tests.Forecast;

public sealed class BillingForecastServiceTests
{
    private readonly IDailyUsageRollupRepository _rollups = Substitute.For<IDailyUsageRollupRepository>();
    private readonly BillingForecastService _service;

    public BillingForecastServiceTests()
    {
        _service = new BillingForecastService(
            _rollups,
            Options.Create(new BillingOptions { DefaultCurrency = "EUR" }));
    }

    [Fact]
    public async Task GetForecastAsync_WithRollups_ProjectsMonthlyFromTrailingAverage()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _rollups
            .GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), null, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(today, Guid.NewGuid(), "gpt-4o", "eng", 0, 0, 7m, 1),
            ]);

        var forecast = await _service.GetForecastAsync(null, trailingDays: 7);

        forecast.TrailingTotalCost.Should().Be(7m);
        forecast.TrailingDays.Should().Be(7);
        forecast.Currency.Should().Be("EUR");
        forecast.ProjectedMonthlyCost.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task GetForecastAsync_NoRollups_ReturnsZeroProjection()
    {
        _rollups
            .GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var forecast = await _service.GetForecastAsync(null, trailingDays: 7);

        forecast.TrailingTotalCost.Should().Be(0m);
        forecast.ProjectedMonthlyCost.Should().Be(0m);
    }

    [Fact]
    public async Task GetForecastAsync_ComputesExactMonthlyProjection()
    {
        const int trailingDays = 7;
        const decimal trailingTotal = 14m;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var expectedMonthly = decimal.Round(trailingTotal / trailingDays * daysInMonth, 4);

        _rollups
            .GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), null, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(today, Guid.NewGuid(), "gpt-4o", null, 0, 0, trailingTotal, 1),
            ]);

        var forecast = await _service.GetForecastAsync(null, trailingDays);

        forecast.ProjectedMonthlyCost.Should().Be(expectedMonthly);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(200, 90)]
    public async Task GetForecastAsync_ClampsTrailingDays(int requestedDays, int expectedDays)
    {
        _rollups
            .GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var forecast = await _service.GetForecastAsync(null, requestedDays);

        forecast.TrailingDays.Should().Be(expectedDays);
    }

    [Fact]
    public async Task GetForecastAsync_PassesTenantFilterToRepository()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-6);

        _rollups
            .GetRollupsAsync(from, today, tenantId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        await _service.GetForecastAsync(tenantId, trailingDays: 7);

        await _rollups
            .Received(1)
            .GetRollupsAsync(from, today, tenantId, Arg.Any<CancellationToken>());
    }
}
