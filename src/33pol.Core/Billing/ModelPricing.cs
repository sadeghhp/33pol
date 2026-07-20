namespace Pol33.Core.Billing;

/// <summary>
/// The admin-editable price for a model, in units of currency per million tokens.
/// </summary>
public sealed class ModelPricing
{
    public decimal InputPricePerMillionTokens { get; set; }

    public decimal OutputPricePerMillionTokens { get; set; }

    /// <summary>Read-only on write requests; reported on GET so the UI can label amounts.</summary>
    public string Currency { get; set; } = "USD";
}

public sealed class ModelPricingUpdateResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public int StatusCode { get; init; } = 200;

    public static ModelPricingUpdateResult Ok(string message) =>
        new() { Success = true, Message = message, StatusCode = 200 };

    public static ModelPricingUpdateResult Fail(string message, int statusCode) =>
        new() { Success = false, Message = message, StatusCode = statusCode };
}
