namespace Pol33.Policy.CircuitBreaker;

public sealed class CircuitBreaker
{
    private readonly CircuitBreakerPolicyOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;
    private bool _halfOpenPermit = true;

    public CircuitBreaker(CircuitBreakerPolicyOptions options, Func<DateTimeOffset>? clock = null)
    {
        _options = options;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
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

    public bool TryEnter()
    {
        lock (_sync)
        {
            if (_state == CircuitState.Open)
            {
                if (_clock() - _openedAt < _options.BreakDuration)
                {
                    return false;
                }

                _state = CircuitState.HalfOpen;
                _halfOpenPermit = true;
            }

            if (_state == CircuitState.HalfOpen)
            {
                if (!_halfOpenPermit)
                {
                    return false;
                }

                _halfOpenPermit = false;
            }

            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
            _halfOpenPermit = true;
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

            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.FailureThreshold)
            {
                TripOpen();
            }
        }
    }

    private void TripOpen()
    {
        _state = CircuitState.Open;
        _openedAt = _clock();
        _consecutiveFailures = 0;
        _halfOpenPermit = true;
    }
}
