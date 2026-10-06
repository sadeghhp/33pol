using Pol33.Core.Billing;
using Pol33.Core.Identity;

namespace Pol33.Core.Observability;

/// <summary>
/// Who a request is attributed to on the Prometheus series that name a caller: the tenant slug and
/// the API key's label.
/// </summary>
/// <remarks>
/// <para>Neither value is a credential. <see cref="Key"/> is the label an operator typed when the
/// key was issued — never the secret, its hash or its prefix — and <see cref="Tenant"/> is the slug,
/// never the tenant id.</para>
///
/// <para>Two keys of one tenant that share a label, or that both have none, are one series. That is
/// the price of a label an operator can read; <c>GET /admin/api/usage/keys</c> still tells them
/// apart.</para>
/// </remarks>
public readonly record struct MetricCaller(string Tenant, string Key)
{
    /// <summary>The <c>key</c> value for traffic that presented no credential.</summary>
    public const string NoKey = "(none)";

    /// <summary>What a caller is folded into once the series budget is spent.</summary>
    public const string Overflow = "other";

    /// <summary>
    /// Longest label value exported. Key labels are free text; a pasted paragraph must not become a
    /// label value on every series the key touches.
    /// </summary>
    public const int MaxLabelLength = 64;

    /// <summary>Keyless traffic: a public model, or a gateway running without authentication.</summary>
    public static MetricCaller Anonymous { get; } = new(BillingMetricLabels.AnonymousTenant, NoKey);

    public static MetricCaller Other { get; } = new(Overflow, Overflow);

    /// <summary>
    /// False for <c>default</c>, which is what an older call site that names no caller hands over.
    /// </summary>
    public bool IsSpecified => Tenant is not null && Key is not null;

    public static MetricCaller From(TenantContext? tenant) =>
        tenant is null || string.IsNullOrWhiteSpace(tenant.TenantId)
            ? Anonymous
            : From(tenant.TenantSlug, tenant.ApiKeyLabel);

    /// <summary>The caller for an authenticated request, from the slug and label as stored.</summary>
    public static MetricCaller From(string? tenantSlug, string? apiKeyLabel) =>
        new(
            Clean(tenantSlug) ?? BillingMetricLabels.UnknownTenant,
            Clean(apiKeyLabel) ?? BillingMetricLabels.UnlabeledKey);

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Trim returns the same instance when there is nothing to trim, so the common case — a
        // short, tidy label — allocates nothing per request.
        var trimmed = value.Trim();
        return trimmed.Length <= MaxLabelLength ? trimmed : trimmed[..MaxLabelLength];
    }
}
