namespace Pol33.Core.Configuration;

public sealed class BillingWebhookOptions
{
    public const string SectionName = "Billing:Webhooks";

    public string? EndpointUrl { get; set; }

    public string Secret { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(EndpointUrl) && !string.IsNullOrWhiteSpace(Secret);
}
