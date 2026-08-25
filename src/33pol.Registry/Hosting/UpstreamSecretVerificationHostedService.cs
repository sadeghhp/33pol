using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pol33.Registry.Services;

namespace Pol33.Registry.Hosting;

/// <summary>
/// Checks at startup that every stored upstream credential can still be decrypted.
/// </summary>
/// <remarks>
/// The key pepper is the encryption key for the upstream secrets file, and rotating it — the
/// documented response to a leaked pepper — makes every previously stored credential permanently
/// unrecoverable. Without this check the only symptom was every request to every authenticated
/// upstream returning "Upstream auth token not configured", one request at a time, with nothing
/// pointing at the rotation as the cause. Reported at startup instead, where an operator can act on
/// it before the instance takes traffic.
/// </remarks>
public sealed class UpstreamSecretVerificationHostedService(
    FileUpstreamSecretStore secretStore,
    ILogger<UpstreamSecretVerificationHostedService> logger,
    UpstreamSecretVerificationState? state = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var (total, undecryptable) = secretStore.VerifyStoredSecrets();
        state?.Record(total, undecryptable);

        if (undecryptable > 0)
        {
            logger.LogError(
                "{Undecryptable} of {Total} stored upstream credential(s) cannot be decrypted with the "
                + "configured Gateway:Security:KeyPepper. Models relying on them will fail every request "
                + "with 'Upstream auth token not configured'. This is the expected result of rotating the "
                + "pepper: re-enter the affected models' upstream API keys in the admin UI.",
                undecryptable,
                total);
        }
        else if (total > 0)
        {
            logger.LogInformation("Verified {Total} stored upstream credential(s).", total);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
