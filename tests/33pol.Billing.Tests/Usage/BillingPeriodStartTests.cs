using Pol33.Billing.Usage;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingPeriodStartTests
{
    [Fact]
    public void GetPeriodStart_MidMonth_ReturnsStartOfCurrentPeriod()
    {
        BillingUsagePersistenceHandler.GetPeriodStart(new DateOnly(2026, 5, 26), 1)
            .Should().Be(new DateOnly(2026, 5, 1));
    }

    [Fact]
    public void GetPeriodStart_BeforePeriodStartDay_ReturnsPreviousMonth()
    {
        BillingUsagePersistenceHandler.GetPeriodStart(new DateOnly(2026, 5, 10), 15)
            .Should().Be(new DateOnly(2026, 4, 15));
    }

    [Fact]
    public void GetPeriodStart_OnPeriodStartDay_ReturnsSameDay()
    {
        BillingUsagePersistenceHandler.GetPeriodStart(new DateOnly(2026, 5, 15), 15)
            .Should().Be(new DateOnly(2026, 5, 15));
    }

    [Fact]
    public void GetPeriodStart_PeriodStartDayAbove28_ClampsTo28InMonth()
    {
        BillingUsagePersistenceHandler.GetPeriodStart(new DateOnly(2026, 2, 28), 31)
            .Should().Be(new DateOnly(2026, 2, 28));
    }

    [Fact]
    public void GetPeriodStart_FebruaryShortMonth_UsesLastValidDay()
    {
        BillingUsagePersistenceHandler.GetPeriodStart(new DateOnly(2026, 3, 5), 28)
            .Should().Be(new DateOnly(2026, 2, 28));
    }
}
