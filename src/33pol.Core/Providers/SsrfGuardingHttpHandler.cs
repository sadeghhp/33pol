namespace Pol33.Core.Providers;

/// <summary>
/// Delegating handler that re-validates the target host of every outbound request against the
/// provider-discovery blocklist, resolving DNS names to their addresses first. Paired with
/// <c>AllowAutoRedirect = false</c> on the primary handler, this prevents SSRF via a public-looking
/// hostname that resolves to an internal address or a redirect to an internal target.
/// </summary>
public sealed class SsrfGuardingHttpHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri)
        {
            var error = await BlockedProviderModelsListHost
                .ValidateResolvedHostAsync(uri, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                throw new HttpRequestException(error);
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
