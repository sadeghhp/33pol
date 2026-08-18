using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Proxy.Hosting;

/// <summary>
/// Flips the gateway into draining on shutdown and holds the process open long enough for load
/// balancers to notice.
/// </summary>
/// <remarks>
/// <para>The drain flag alone bought nothing. It was raised on <c>ApplicationStopping</c>, which
/// fires at the same moment the server stops accepting connections — so a load balancer, which only
/// learns via its readiness probe on a polling interval, kept routing traffic to an instance that
/// was already tearing down. Every rolling restart dropped requests.</para>
///
/// <para>The fix is to make readiness go unhealthy first and then keep serving for a grace period,
/// so in-flight requests finish and the balancer removes this instance before Kestrel closes.
/// <see cref="GatewayResilienceOptions.ShutdownDrainSeconds"/> should be a small multiple of the
/// readiness probe interval, and the host's own shutdown timeout must exceed it.</para>
///
/// <para>The drain has to run while Kestrel is still listening, and hosted-service
/// <see cref="IHostedService.StopAsync"/> is too late for that: <c>WebApplication.CreateBuilder</c>
/// registers the server's hosted service after all user services and the host stops services in
/// reverse registration order, so the server has already unbound by the time this service's
/// <c>StopAsync</c> runs. <see cref="IHostedLifecycleService.StoppingAsync"/> is awaited for every
/// hosted service before any <c>StopAsync</c> starts, which is the last point at which readiness
/// probes are still answered — so that is where the drain flag is raised and the window is held.</para>
/// </remarks>
public sealed class GatewayShutdownHostedService : IHostedLifecycleService
{
    private readonly IGatewayDrainState _drainState;
    private readonly TimeSpan _drainDuration;
    private readonly ILogger<GatewayShutdownHostedService> _logger;

    public GatewayShutdownHostedService(
        IGatewayDrainState drainState,
        IOptions<GatewayOptions> options,
        ILogger<GatewayShutdownHostedService> logger)
    {
        _drainState = drainState;
        _drainDuration = TimeSpan.FromSeconds(Math.Max(0, options.Value.Resilience.ShutdownDrainSeconds));
        _logger = logger;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <remarks>
    /// Runs as an awaited lifecycle step rather than an <c>ApplicationStopping</c> callback, because
    /// lifecycle steps are awaited — a callback returning a task is not, so any delay inside one is
    /// ignored and the drain window never actually happens. It is the <em>stopping</em> step rather
    /// than <see cref="StopAsync"/> so that it completes before the server's own stop step unbinds
    /// the listener (see the class remarks).
    /// </remarks>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        _drainState.BeginDrain();

        if (_drainDuration <= TimeSpan.Zero)
        {
            return;
        }

        _logger.LogInformation(
            "Draining: readiness now reports unhealthy and new inference requests are refused. "
            + "Holding for {DrainSeconds}s so load balancers can deregister this instance.",
            _drainDuration.TotalSeconds);

        try
        {
            await Task.Delay(_drainDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown deadline hit first; stop immediately rather than block termination.
            _logger.LogWarning(
                "Drain window cut short by the host shutdown timeout. Raise the host's shutdown timeout "
                + "above Gateway:Resilience:ShutdownDrainSeconds ({DrainSeconds}s) to drain fully.",
                _drainDuration.TotalSeconds);
        }
    }

    /// <remarks>
    /// The drain already happened in <see cref="StoppingAsync"/>. Raising the flag again is a harmless
    /// idempotent no-op that only matters for hosts that stop services without running the lifecycle
    /// steps; there is deliberately no second delay here — by now the server is gone.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _drainState.BeginDrain();
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
