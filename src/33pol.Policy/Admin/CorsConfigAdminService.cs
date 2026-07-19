using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Policy.Admin;

/// <summary>
/// Reads CORS origins from the live config snapshot and persists updates to the database, forcing an
/// in-process snapshot refresh so a change is live without a restart. Requires a configured database;
/// in a DB-less deployment CORS origins are read-only from appsettings.
/// </summary>
public sealed class CorsConfigAdminService(
    IGatewayConfigProvider configProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<CorsConfigAdminService> logger) : ICorsConfigAdminService
{
    public string[] GetCurrent() => [.. configProvider.Current.Cors.AllowedOrigins];

    public async Task<CorsConfigUpdateResult> UpdateAsync(
        IReadOnlyList<string> allowedOrigins,
        CancellationToken cancellationToken = default)
    {
        if (!CorsConfigValidation.TryValidate(allowedOrigins, out var validationError, out var normalized))
        {
            return CorsConfigUpdateResult.Fail(validationError!, statusCode: 400);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetService<ICorsSettingsRepository>();
        if (repository is null)
        {
            return CorsConfigUpdateResult.Fail(
                "CORS updates require a configured database.",
                statusCode: 503);
        }

        try
        {
            await repository.SaveAllowedOriginsAsync(normalized, cancellationToken).ConfigureAwait(false);

            // Force an immediate in-process reload so the new origins are live without waiting for the
            // reconcile poll.
            var refresher = scope.ServiceProvider.GetService<IGatewayConfigRefresher>();
            if (refresher is not null)
            {
                await refresher.RefreshNowAsync(cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation("Updated CORS allowed origins ({OriginCount}).", normalized.Length);
            return CorsConfigUpdateResult.Ok("CORS allowed origins updated.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist CORS configuration.");
            return CorsConfigUpdateResult.Fail("Failed to persist CORS configuration.", statusCode: 500);
        }
    }
}
