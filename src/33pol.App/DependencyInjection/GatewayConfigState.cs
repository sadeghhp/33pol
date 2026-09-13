using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.App.DependencyInjection;

/// <summary>
/// Holds the current configuration snapshot behind a lock-free volatile reference. The syncer swaps
/// in a new immutable snapshot with <see cref="Set"/>; the hot path reads <see cref="Current"/>
/// without locking and never sees a torn update. Starts at the appsettings-derived snapshot so
/// reads are safe before the first database load.
///
/// <para>Environment CORS origins (<c>GATEWAY_CORS_ALLOWED_ORIGIN_*</c> /
/// <c>GATEWAY_CORS_ALLOWED_ORIGINS</c>) are overlaid on <em>every</em> snapshot that passes through
/// here — the initial one and each database load. Before, they were only written into the options
/// copy that seeds the database on first boot, so a database deployment ignored them from the
/// second boot on and a database-less one ignored them always, while the operator docs promised
/// that editing <c>.env</c> and recreating the container was enough.</para>
/// </summary>
internal sealed class GatewayConfigState : IGatewayConfigProvider
{
    private readonly IReadOnlyList<string> _environmentOrigins;
    private readonly TimeProvider _timeProvider;
    private readonly object _projectLock = new();
    private long _effectiveVersion;
    private GatewayConfigSnapshot _stored;
    private volatile Projection _current;

    public GatewayConfigState(GatewayConfigSnapshot initial)
        : this(initial, [])
    {
    }

    /// <param name="initial">The appsettings-derived snapshot.</param>
    /// <param name="environmentOrigins">
    /// Origins from the environment, already normalized; merged ahead of the snapshot's own list.
    /// </param>
    /// <param name="timeProvider">
    /// The clock schedule windows are evaluated against. Optional so hand-built states keep
    /// compiling; absent means the system clock.
    /// </param>
    public GatewayConfigState(
        GatewayConfigSnapshot initial,
        IReadOnlyList<string> environmentOrigins,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(environmentOrigins);
        _environmentOrigins = environmentOrigins;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _stored = Overlay(initial);
        _current = Project(_stored, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// The snapshot with every scheduled window applied for the current instant.
    /// </summary>
    /// <remarks>
    /// Scheduled tiers are projected lazily, here, rather than by a timer: this property is read on
    /// every request, so re-projecting the moment a window boundary has passed is exactly as
    /// prompt as any timer could be and needs nothing to keep running. Without schedules the check
    /// is one comparison against a sentinel and no clock read at all.
    /// </remarks>
    public GatewayConfigSnapshot Current
    {
        get
        {
            var projection = _current;
            if (projection.NextTransition == DateTimeOffset.MaxValue)
            {
                return projection.Snapshot;
            }

            var now = _timeProvider.GetUtcNow();
            return now < projection.NextTransition ? projection.Snapshot : Reproject(now);
        }
    }

    /// <summary>The snapshot as stored: base tiers and schedules, before any window is applied.</summary>
    public GatewayConfigSnapshot Stored => _stored;

    public void Set(GatewayConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_projectLock)
        {
            _stored = Overlay(snapshot);
            _current = Project(_stored, _timeProvider.GetUtcNow());
        }
    }

    private GatewayConfigSnapshot Reproject(DateTimeOffset now)
    {
        lock (_projectLock)
        {
            // Another reader may have re-projected while this one waited for the lock.
            var projection = _current;
            if (now < projection.NextTransition)
            {
                return projection.Snapshot;
            }

            _current = Project(_stored, now);
            return _current.Snapshot;
        }
    }

    private Projection Project(GatewayConfigSnapshot stored, DateTimeOffset now)
    {
        if (!stored.RateLimits.HasSchedules)
        {
            return new Projection(stored, DateTimeOffset.MaxValue);
        }

        var version = Interlocked.Increment(ref _effectiveVersion);
        var (effective, next) = RateLimitScheduleProjection.Project(stored.RateLimits, now, version);
        return new Projection(stored with { RateLimits = effective }, next ?? DateTimeOffset.MaxValue);
    }

    private sealed record Projection(GatewayConfigSnapshot Snapshot, DateTimeOffset NextTransition);

    /// <summary>
    /// Environment origins first, then the snapshot's, de-duplicated. A union rather than a
    /// replacement so an origin added through the admin console is not silently dropped on the
    /// next boot of a container that also carries origins in <c>.env</c>.
    /// </summary>
    private GatewayConfigSnapshot Overlay(GatewayConfigSnapshot snapshot)
    {
        if (_environmentOrigins.Count == 0)
        {
            return snapshot;
        }

        var merged = GatewayCorsOptions.NormalizeOrigins(
            _environmentOrigins.Concat(snapshot.Cors.AllowedOrigins));

        return snapshot with
        {
            Cors = snapshot.Cors with { AllowedOrigins = merged },
        };
    }
}
