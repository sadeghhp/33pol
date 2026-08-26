namespace Pol33.Policy.CircuitBreaker;

/// <summary>
/// Per-backend circuit breaker driven by the failure rate over a rolling window.
/// </summary>
/// <remarks>
/// Outcomes are counted over <see cref="CircuitBreakerPolicyOptions.SamplingWindow"/> and the
/// breaker opens once the window holds at least
/// <see cref="CircuitBreakerPolicyOptions.FailureThreshold"/> failures <em>and</em> those failures
/// are at least <see cref="CircuitBreakerPolicyOptions.FailureRatioThreshold"/> of all outcomes.
/// Requiring both means a low-traffic backend still trips on a handful of failures, while a busy
/// healthy one is not opened by an absolute count it reaches while mostly succeeding.
/// </remarks>
public sealed class CircuitBreaker
{
    private readonly CircuitBreakerPolicyOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();

    // Bounded ring of recent outcomes. Sized generously relative to the threshold: only the window
    // matters for the decision, and capping the ring keeps a high-throughput backend from growing
    // this without limit between evictions.
    private readonly (DateTimeOffset At, bool Failed)[] _outcomes;
    private int _outcomeStart;
    private int _outcomeCount;

    private CircuitState _state = CircuitState.Closed;
    private DateTimeOffset _openedAt;
    private bool _halfOpenPermit = true;
    private DateTimeOffset _halfOpenPermitTakenAt;

    public CircuitBreaker(CircuitBreakerPolicyOptions options, Func<DateTimeOffset>? clock = null)
    {
        _options = options;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _outcomes = new (DateTimeOffset, bool)[Math.Max(16, Math.Max(1, options.FailureThreshold) * 8)];
    }

    public CircuitState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// A consistent read of the breaker for the admin Overview: the state, when it opened, how the
    /// sampling window currently looks and how long an open breaker has left before it probes.
    /// </summary>
    public CircuitBreakerSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var now = _clock();
            var (failures, total) = _state == CircuitState.Open ? (0, 0) : CountWindow();
            var opened = _state == CircuitState.Open || _state == CircuitState.HalfOpen ? _openedAt : (DateTimeOffset?)null;
            TimeSpan? remaining = _state == CircuitState.Open
                ? TimeSpan.FromTicks(Math.Max(0, (_openedAt + _options.BreakDuration - now).Ticks))
                : null;
            return new CircuitBreakerSnapshot(_state, opened, failures, total, remaining);
        }
    }

    /// <remarks>
    /// In <see cref="CircuitState.HalfOpen"/> this consumes the single probe permit, so every other
    /// caller is refused until the probe reports an outcome — or until it is presumed stalled, see
    /// <see cref="CircuitBreakerPolicyOptions.HalfOpenProbeTimeout"/>.
    /// </remarks>
    public bool TryEnter()
    {
        lock (_sync)
        {
            var now = _clock();

            if (_state == CircuitState.Open)
            {
                if (now - _openedAt < _options.BreakDuration)
                {
                    return false;
                }

                _state = CircuitState.HalfOpen;
                _halfOpenPermit = true;
            }

            if (_state == CircuitState.HalfOpen)
            {
                // A probe holding the permit past the deadline is presumed stalled and the permit is
                // reclaimed for this caller. Without this the breaker refused every other request for
                // as long as the probe ran, and on this gateway a probe is a generation that can
                // legitimately take minutes: a model that was merely slow answered nothing at all,
                // for far longer than BreakDuration.
                //
                // A reclaimed probe that later reports an outcome is still believed — evidence about
                // the backend is evidence whenever it arrives. The only looseness this admits is that
                // its RecordAbandoned can hand back a permit the newer probe holds, which at worst
                // lets one extra probe through; admissions remain bounded at roughly one per timeout,
                // and the breaker can no longer wedge.
                if (!_halfOpenPermit && now - _halfOpenPermitTakenAt < _options.HalfOpenProbeTimeout)
                {
                    return false;
                }

                _halfOpenPermit = false;
                _halfOpenPermitTakenAt = now;
            }

            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_sync)
        {
            // A probe that succeeds closes the breaker and clears the window it was judged on, so
            // the failures that opened it cannot immediately re-trip it.
            if (_state == CircuitState.HalfOpen)
            {
                _state = CircuitState.Closed;
                _halfOpenPermit = true;
                _outcomeCount = 0;
                _outcomeStart = 0;
                return;
            }

            // Deliberately does NOT force the state to Closed. A slow request that started before
            // the breaker re-opened used to slam it shut on completion, discarding a decision made
            // on newer evidence.
            Append(failed: false);
        }
    }

    /// <summary>
    /// Releases a half-open probe permit taken by <see cref="TryEnter"/> without recording an
    /// outcome. Used when an admitted request ends for a reason that says nothing about backend
    /// health (client abort, gateway-side rejection, configuration error). Without this the permit
    /// is never restored and the breaker stays HalfOpen with no probe available, rejecting every
    /// subsequent request until the process restarts.
    /// </summary>
    public void RecordAbandoned()
    {
        lock (_sync)
        {
            if (_state == CircuitState.HalfOpen)
            {
                _halfOpenPermit = true;
            }
        }
    }

    public void RecordFailure()
    {
        lock (_sync)
        {
            if (_state == CircuitState.HalfOpen)
            {
                TripOpen();
                return;
            }

            Append(failed: true);

            var (failures, total) = CountWindow();
            if (failures >= _options.FailureThreshold &&
                total > 0 &&
                (double)failures / total >= _options.FailureRatioThreshold)
            {
                TripOpen();
            }
        }
    }

    private void Append(bool failed)
    {
        var now = _clock();
        EvictExpired(now);

        if (_outcomeCount == _outcomes.Length)
        {
            // Ring full: drop the oldest.
            _outcomes[_outcomeStart] = (now, failed);
            _outcomeStart = (_outcomeStart + 1) % _outcomes.Length;
            return;
        }

        _outcomes[(_outcomeStart + _outcomeCount) % _outcomes.Length] = (now, failed);
        _outcomeCount++;
    }

    private void EvictExpired(DateTimeOffset now)
    {
        var cutoff = now - _options.SamplingWindow;
        while (_outcomeCount > 0 && _outcomes[_outcomeStart].At < cutoff)
        {
            _outcomeStart = (_outcomeStart + 1) % _outcomes.Length;
            _outcomeCount--;
        }
    }

    private (int Failures, int Total) CountWindow()
    {
        EvictExpired(_clock());

        var failures = 0;
        for (var i = 0; i < _outcomeCount; i++)
        {
            if (_outcomes[(_outcomeStart + i) % _outcomes.Length].Failed)
            {
                failures++;
            }
        }

        return (failures, _outcomeCount);
    }

    private void TripOpen()
    {
        _state = CircuitState.Open;
        _openedAt = _clock();
        _outcomeCount = 0;
        _outcomeStart = 0;
        _halfOpenPermit = true;
        _halfOpenPermitTakenAt = default;
    }
}
