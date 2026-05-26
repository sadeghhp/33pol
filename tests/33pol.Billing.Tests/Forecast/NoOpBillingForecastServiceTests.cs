using Pol33.Billing.Forecast;

namespace Pol33.Billing.Tests.Forecast;

public sealed class NoOpBillingForecastServiceTests
{
    private readonly NoOpBillingForecastService _service = new();

    [Fact]
    public async Task GetForecastAsync_ReturnsZeroCosts()
    {
        var forecast = await _service.GetForecastAsync(null, trailingDays: 14);

        forecast.TrailingDays.Should().Be(14);
        forecast.TrailingTotalCost.Should().Be(0m);
        forecast.ProjectedMonthlyCost.Should().Be(0m);
        forecast.Currency.Should().Be("USD");
    }
}
