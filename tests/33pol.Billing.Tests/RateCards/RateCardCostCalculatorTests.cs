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

    /// <summary>
    /// When the upstream reports only a combined total, the input/output split is unknown. Pricing
    /// it at the input rate (the old behaviour) under-billed by the rate ratio — 4x for this card.
    /// The policy is the conservative one already used to bound reserved cost: the dearer rate.
    /// </summary>
    [Fact]
    public void CalculateFromTotalTokens_UsesTheHigherOfInputAndOutputRates()
    {
        var result = _calculator.CalculateFromTotalTokens(Gpt4oRateCard, totalTokens: 1_000_000);

        result.TotalCost.Should().Be(10.00m, "output is the dearer side of this card");
        result.TotalCost.Should().NotBe(2.50m, "pricing at the input rate is the defect being fixed");
        result.Currency.Should().Be("USD");
    }

    [Fact]
    public void CalculateFromTotalTokens_WhenInputIsDearer_UsesTheInputRate()
    {
        var inputHeavyCard = Gpt4oRateCard with
        {
            InputPricePerMillionTokens = 15.00m,
            OutputPricePerMillionTokens = 3.00m,
        };

        _calculator.CalculateFromTotalTokens(inputHeavyCard, totalTokens: 1_000_000)
            .TotalCost.Should().Be(15.00m);
    }

    [Fact]
    public void CalculateFromTotalTokens_ZeroTokens_IsFree()
    {
        _calculator.CalculateFromTotalTokens(Gpt4oRateCard, totalTokens: 0).TotalCost.Should().Be(0m);
    }

    [Fact]
    public void CalculateFromTotalTokens_NegativeTokens_Throws()
    {
        var act = () => _calculator.CalculateFromTotalTokens(Gpt4oRateCard, totalTokens: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// A real split must keep using the per-side rates: the conservative policy applies only where
    /// the split is genuinely unknown.
    /// </summary>
    [Fact]
    public void Calculate_SplitUsage_StillPricesEachSideAtItsOwnRate()
    {
        var result = _calculator.Calculate(Gpt4oRateCard, promptTokens: 1_000_000, completionTokens: 1_000_000);

        result.InputCost.Should().Be(2.50m);
        result.OutputCost.Should().Be(10.00m);
        result.TotalCost.Should().Be(12.50m);
    }

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
    public void CalculateLineCost_ZeroTokens_ReturnsZero()
    {
        RateCardCostCalculator.CalculateLineCost(0, 5m).Should().Be(0m);
    }

    [Fact]
    public void Calculate_NullRateCard_Throws()
    {
        var act = () => _calculator.Calculate(null!, 1, 1);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Calculate_FractionalMillionTokens_RoundsToSixDecimals()
    {
        var rateCard = Gpt4oRateCard with { InputPricePerMillionTokens = 0.50m };

        var result = _calculator.Calculate(rateCard, promptTokens: 123_456, completionTokens: 0);

        result.InputCost.Should().Be(0.061728m);
    }
}
