using Microsoft.AspNetCore.Http;
using Pol33.Core.Identity;

namespace Pol33.Security.Identity;

public static class TenantContextHttpExtensions
{
    public const string HttpContextItemKey = TenantContextKeys.HttpContextItemKey;

    public static TenantContext? GetTenantContext(this HttpContext context) =>
        context.Items.TryGetValue(HttpContextItemKey, out var value) ? value as TenantContext : null;

    public static void SetTenantContext(this HttpContext context, TenantContext tenantContext) =>
        context.Items[HttpContextItemKey] = tenantContext;
}
