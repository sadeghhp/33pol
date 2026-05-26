using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;

namespace Pol33.Proxy.Hosting;

public sealed class GatewayShutdownHostedService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IGatewayDrainState _drainState;
    private IDisposable? _registration;

    public GatewayShutdownHostedService(
        IHostApplicationLifetime lifetime,
        IGatewayDrainState drainState)
    {
        _lifetime = lifetime;
        _drainState = drainState;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = _lifetime.ApplicationStopping.Register(_drainState.BeginDrain);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        _drainState.BeginDrain();
        return Task.CompletedTask;
    }
}
