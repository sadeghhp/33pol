using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;

namespace Pol33.App.Cors;

/// <summary>
/// Builds the default CORS policy from live <see cref="GatewayOptions"/> so admin/appsettings
/// reloads take effect without a process restart.
/// </summary>
public sealed class GatewayCorsPolicyProvider(
    IOptionsMonitor<GatewayOptions> optionsMonitor,
    IHostEnvironment environment) : ICorsPolicyProvider
{
    public const int PreflightMaxAgeSeconds = 86_400;

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        _ = policyName;
        return Task.FromResult<CorsPolicy?>(BuildPolicy());
    }

    private CorsPolicy BuildPolicy()
    {
        var builder = new CorsPolicyBuilder();

        if (environment.IsDevelopment())
        {
            builder.AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
            return builder.Build();
        }

        var allowedOrigins = optionsMonitor.CurrentValue.Cors.GetNormalizedOrigins();
        if (allowedOrigins.Length > 0)
        {
            builder.SetIsOriginAllowed(origin => CorsOriginMatcher.IsOriginAllowed(origin, allowedOrigins))
                .AllowAnyHeader()
                .AllowAnyMethod()
                .SetPreflightMaxAge(TimeSpan.FromSeconds(PreflightMaxAgeSeconds));
            return builder.Build();
        }

        builder.SetIsOriginAllowed(_ => false);
        return builder.Build();
    }
}
