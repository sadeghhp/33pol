using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Webhooks;

public sealed class BillingWebhookDispatcher(
    IHttpClientFactory httpClientFactory,
    IOptions<BillingWebhookOptions> options,
    ILogger<BillingWebhookDispatcher> logger) : IBillingWebhookDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task DispatchAsync(
        string eventType,
        object payload,
        CancellationToken cancellationToken = default)
    {
        var webhook = options.Value;
        if (!webhook.IsConfigured)
        {
            return;
        }

        var body = JsonSerializer.Serialize(
            new { type = eventType, timestamp = DateTimeOffset.UtcNow, data = payload },
            JsonOptions);
        var signature = ComputeSignature(body, webhook.Secret);

        try
        {
            var client = httpClientFactory.CreateClient(nameof(BillingWebhookDispatcher));
            using var request = new HttpRequestMessage(HttpMethod.Post, webhook.EndpointUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-33pol-Signature", signature);

            var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Webhook {EventType} failed with status {StatusCode}",
                    eventType,
                    response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Webhook {EventType} dispatch error", eventType);
        }
    }

    internal static string ComputeSignature(string body, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(body);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
