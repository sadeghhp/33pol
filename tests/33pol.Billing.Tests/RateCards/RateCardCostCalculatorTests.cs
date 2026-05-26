using Pol33.Billing.RateCards;
using Pol33.Core.Billing;

namespace Pol33.Billing.Tests.RateCards;

public sealed class RateCardCostCalculatorTests
{
    private static readonly RateCardRecord Gpt4oRateCard = new(
        Guid.NewGuid(),
        "gpt4o-standard",
        "GPT-4o Standard",
        "gpt-4o",
        InputPricePerMillionTokens: 2.50m,
        OutputPricePerMillionTokens: 10.00m,
        Currency: "USD",
        EffectiveFrom: DateTimeOffset.UtcNow,
        EffectiveUntil: null,
        IsActive: true,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private readonly RateCardCostCalculator _calculator = new();

    [Fact]
    public void Calculate_OneMillionInputTokens_ChargesInputRate()
    {
        var result = _calculator.Calculate(Gpt4oRateCard, promptTokens: 1_000_000, completionTokens: 0);

        result.InputCost.Should().Be(2.50m);
        result.OutputCost.Should().Be(0m);
        result.TotalCost.Should().Be(2.50m);
        result.Currency.Should().Be("USD");
    }

    [Fact]
    public void Calculate_MixedTokens_SumsInputAndOutputCosts()
    {
        var result = _calculator.Calculate(Gpt4oRateCard, promptTokens: 500_000, completionTokens: 250_000);

        result.InputCost.Should().Be(1.25m);
        result.OutputCost.Should().Be(2.50m);
        result.TotalCost.Should().Be(3.75m);
    }

    [Fact]
    public void Calculate_ZeroTokens_ReturnsZeroCost()
    {
        var result = _calculator.Calculate(Gpt4oRateCard, promptTokens: 0, completionTokens: 0);

        result.TotalCost.Should().Be(0m);
    }

    [Fact]
    public void Calculate_NegativeTokens_Throws()
    {
        var act = () => _calculator.Calculate(Gpt4oRateCard, promptTokens: -1, completionTokens: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Calculate_FractionalMillionTokens_RoundsToSixDecimals()
    {
        var rateCard = Gpt4oRateCard with { InputPricePerMillionTokens = 0.50m };

        var result = _calculator.Calculate(rateCard, promptTokens: 123_456, completionTokens: 0);

        result.InputCost.Should().Be(0.061728m);
    }
}
