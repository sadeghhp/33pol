using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Policy.RateLimiting;

/// <summary>
/// Load-aware enforcement: watches how saturated each model is and how hard each caller is retrying,
/// and moves two narrow levers inside bounds the operator set.
/// </summary>
/// <remarks>
/// <para><b>What it reads.</b> Model pressure comes from the per-model bulkhead — how many forwards
/// are in flight against the model's ceiling, and how full its wait queue is — and from the circuit
/// breaker. Those are the gateway's existing, already-maintained measures of an upstream being at
/// capacity; deriving pressure from them costs nothing extra and means the governor and the operator
/// are looking at the same numbers. A model whose breaker is open is treated as fully saturated
/// regardless of occupancy, because a breaker only opens after the upstream has already been
/// failing.</para>
///
/// <para><b>What it does.</b> Model factors move by additive-increase / multiplicative-decrease,
/// with a hold band between the two watermarks. Cutting hard and recovering slowly is what makes a
/// feedback controller converge rather than oscillate, and the hold band stops a model that is
/// sitting near a watermark from being adjusted on every tick. Every factor is clamped to
/// <c>[MinFactor, 1.0]</c>, so the worst adaptation can do is enforce a quarter of the configured
/// rate — a degradation, never an outage, and never an increase.</para>
///
/// <para><b>Why backoff is separate.</b> A client in a retry storm is the most expensive rejected
/// traffic there is: it pays the full admission cost, is refused, and immediately returns. Cutting
/// its rate limit does not help — it is already being refused. Telling it to wait longer is the only
/// response that reduces load, so persistent rejection escalates that partition's
/// <c>Retry-After</c> geometrically to a ceiling. It resets on the first admitted request, which is
/// what keeps a legitimately bursty client from ever noticing it.</para>
///
/// <para><b>Bounds.</b> Every adjustment is capped; nothing here can raise a limit, block a caller,
/// or hold a client longer than the configured maximum. The whole state is readable through
/// <see cref="Snapshot"/> with a sentence explaining each model's last move.</para>
/// </remarks>
public sealed class AdaptiveRateLimitGovernor : IAdaptiveRateLimitGovernor
{
    /// <summary>
    /// Ceiling on tracked partitions. One entry per partition being refused, and the partition key
    /// for anonymous traffic is a client address block, so without a ceiling a spray of rejected
    /// requests from many sources grows this table without bound between maintenance ticks — the
    /// exact failure the store's own partition table is capped against.
    /// </summary>
    private const int MaxBackoffPartitions = 20_000;

    private readonly IGatewayConfigProvider _configProvider;
    private readonly IBulkheadStateSource? _bulkheads;
    private readonly ICircuitBreakerStateSource? _breakers;
    private readonly ILogger<AdaptiveRateLimitGovernor> _logger;
    private readonly AdaptiveRateLimitOptions _options;

    private readonly ConcurrentDictionary<string, ModelFactor> _modelFactors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PartitionBackoff> _backoff = new(StringComparer.Ordinal);

    // Tracked alongside the dictionary rather than read from it: ConcurrentDictionary.Count takes
    // every bucket lock, and this is consulted on the request path once per rejection.
    private int _backoffCount;

    private long _lastEvaluatedTicks;

    public AdaptiveRateLimitGovernor(
        IGatewayConfigProvider configProvider,
        ILogger<AdaptiveRateLimitGovernor> logger,
        IOptions<RateLimitingOptions>? options = null,
        IBulkheadStateSource? bulkheads = null,
        ICircuitBreakerStateSource? breakers = null)
    {
        _configProvider = configProvider;
        _logger = logger;
        _bulkheads = bulkheads;
        _breakers = breakers;
        _options = Sanitize(options?.Value.Adaptive ?? new AdaptiveRateLimitOptions());
    }

    /// <summary>
    /// On when both the appsettings switch and the live snapshot's switch are on. Two switches
    /// because they answer different questions: the first is "was this deployment built to adapt",
    /// the second is "should it be adapting right now" — and the second has to be reachable from the
    /// admin UI at three in the morning without a restart.
    /// </summary>
    public bool IsEnabled =>
        _options.Enabled && _configProvider.Current.RateLimits.AdaptiveEnabled;

    public double GetModelFactor(string modelId)
    {
        if (!IsEnabled || string.IsNullOrEmpty(modelId))
        {
            return 1.0;
        }

        return _modelFactors.TryGetValue(modelId, out var state) ? state.Factor : 1.0;
    }

    public int GetRetryAfterSeconds(string partitionKey, int baseRetryAfterSeconds, DateTimeOffset now)
    {
        var floor = Math.Max(1, baseRetryAfterSeconds);
        if (!IsEnabled)
        {
            return floor;
        }

        var seconds = (double)floor;

        if (_backoff.TryGetValue(partitionKey, out var backoff))
        {
            var over = backoff.ConsecutiveRejections - _options.BackoffAfterConsecutiveRejections;
            if (over > 0)
            {
                // Compounding, but only past the threshold, so an ordinary overshoot is still told
                // to wait the usual second. Capped both by the exponent and by the ceiling below;
                // Math.Pow on a bounded exponent cannot overflow into the cast.
                var exponent = Math.Min(over, 32);
                seconds = floor * Math.Pow(_options.BackoffGrowthFactor, exponent);
            }
        }

        seconds = Math.Min(seconds, _options.MaxRetryAfterSeconds);

        if (_options.RetryAfterJitter > 0)
        {
            // Spread downwards only, never below the floor: a crowd refused in the same second must
            // not all return in the same later second, and jittering upwards would mean sometimes
            // telling a client to wait longer than the ceiling allows.
            var spread = seconds * _options.RetryAfterJitter;
            seconds -= Random.Shared.NextDouble() * spread;
        }

        return Math.Max(floor, (int)Math.Ceiling(seconds));
    }

    public void RecordOutcome(string partitionKey, bool admitted, DateTimeOffset now)
    {
        if (!IsEnabled || string.IsNullOrEmpty(partitionKey))
        {
            return;
        }

        if (admitted)
        {
            // Cleared rather than decremented: one success means the client is inside its budget
            // again, and holding a penalty past that would punish recovery.
            if (_backoff.TryRemove(partitionKey, out _))
            {
                Interlocked.Decrement(ref _backoffCount);
            }

            return;
        }

        if (_backoff.TryGetValue(partitionKey, out var existing))
        {
            existing.RecordRejection(now);
            return;
        }

        // At the ceiling a partition simply is not tracked, rather than an existing one being
        // evicted to make room: eviction here would let a flood of one-off sources push out the
        // repeat offender the escalation exists for. An untracked partition still gets the bucket's
        // own Retry-After, just not a lengthened one, and the maintenance tick trims the table back
        // under the ceiling within a few seconds.
        if (Volatile.Read(ref _backoffCount) >= MaxBackoffPartitions)
        {
            return;
        }

        var created = new PartitionBackoff();
        var entry = _backoff.GetOrAdd(partitionKey, created);
        if (ReferenceEquals(entry, created))
        {
            Interlocked.Increment(ref _backoffCount);
        }

        entry.RecordRejection(now);
    }

    public void Evaluate(DateTimeOffset now)
    {
        if (!IsEnabled)
        {
            // Factors are dropped rather than frozen, so switching adaptation off restores the
            // configured limits immediately instead of leaving models pinned at their last
            // reduction with nothing left to lift them.
            if (!_modelFactors.IsEmpty)
            {
                _modelFactors.Clear();
                _backoff.Clear();
                Interlocked.Exchange(ref _backoffCount, 0);
                _logger.LogInformation("Adaptive rate limiting disabled; model factors reset to configured limits.");
            }

            return;
        }

        Interlocked.Exchange(ref _lastEvaluatedTicks, now.UtcTicks);

        var openBreakers = ReadOpenBreakers();

        foreach (var state in _bulkheads?.GetStates() ?? [])
        {
            var saturation = Saturation(state);
            var breakerOpen = openBreakers?.Contains(state.ModelId) == true;
            if (breakerOpen)
            {
                saturation = Math.Max(saturation, 1.0);
            }

            UpdateModel(state.ModelId, saturation, breakerOpen, now);
        }

        ExpireBackoff(now);
        EnforceBackoffCeiling();
    }

    public AdaptiveRateLimitSnapshot Snapshot()
    {
        if (!_options.Enabled)
        {
            return AdaptiveRateLimitSnapshot.Disabled;
        }

        var models = _modelFactors
            .Select(pair => new AdaptiveModelState(
                pair.Key,
                pair.Value.Factor,
                pair.Value.Saturation,
                pair.Value.Reason,
                pair.Value.UpdatedUtc))
            .OrderBy(row => row.Factor)
            .ThenBy(row => row.ModelId, StringComparer.Ordinal)
            .ToArray();

        var lastEvaluated = Interlocked.Read(ref _lastEvaluatedTicks);

        return new AdaptiveRateLimitSnapshot(
            IsEnabled,
            models,
            Volatile.Read(ref _backoffCount),
            lastEvaluated == 0 ? null : new DateTimeOffset(lastEvaluated, TimeSpan.Zero));
    }

    /// <summary>
    /// How full a model's forwarding capacity is, as the larger of its in-flight occupancy and its
    /// queue occupancy.
    /// </summary>
    /// <remarks>
    /// The maximum rather than a blend, because the two saturate in sequence: slots fill first and
    /// only then does the queue start growing, so averaging them would report a model with a full
    /// queue as half-loaded — exactly the state adaptation exists to catch. A model with no
    /// configured ceiling reports zero: unbounded concurrency means the bulkhead is not the
    /// constraint, and inventing a saturation for it would have the governor throttling on a signal
    /// that does not mean anything.
    /// </remarks>
    private static double Saturation(BulkheadModelState state)
    {
        var inflight = state.MaxConcurrent > 0 ? (double)state.InFlight / state.MaxConcurrent : 0;
        var queued = state.MaxQueued > 0 ? (double)state.Queued / state.MaxQueued : 0;
        return Math.Max(inflight, queued);
    }

    private HashSet<string>? ReadOpenBreakers()
    {
        var states = _breakers?.GetStates();
        if (states is null || states.Count == 0)
        {
            return null;
        }

        HashSet<string>? open = null;
        foreach (var state in states)
        {
            // 2 == open; see ICircuitBreakerStateSource. A half-open breaker is deliberately not
            // counted: it is already admitting a trickle to test recovery, and piling a rate
            // reduction on top would slow the recovery it is measuring.
            if (state.State == 2)
            {
                open ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                open.Add(state.ModelId);
            }
        }

        return open;
    }

    private void UpdateModel(string modelId, double saturation, bool breakerOpen, DateTimeOffset now)
    {
        var current = _modelFactors.TryGetValue(modelId, out var existing) ? existing.Factor : 1.0;
        double next;
        string reason;

        if (saturation >= _options.HighWatermark)
        {
            next = Math.Max(_options.MinFactor, current * _options.DecreaseFactor);
            reason = breakerOpen
                ? "circuit breaker open"
                : $"saturation {saturation:P0} at or above the {_options.HighWatermark:P0} high watermark";
        }
        else if (saturation <= _options.LowWatermark)
        {
            next = Math.Min(1.0, current + _options.IncreaseStep);
            reason = $"saturation {saturation:P0} at or below the {_options.LowWatermark:P0} low watermark";
        }
        else
        {
            // The hold band. Recording the saturation without moving the factor is what keeps a
            // model hovering near a watermark from being adjusted every tick.
            if (existing is not null)
            {
                _modelFactors[modelId] = existing with { Saturation = saturation };
            }

            return;
        }

        if (next >= 1.0 && current >= 1.0)
        {
            // Fully recovered and staying there: drop the entry so an idle model does not sit in the
            // report claiming to be adapted at 1.0.
            _modelFactors.TryRemove(modelId, out _);
            return;
        }

        if (Math.Abs(next - current) > 0.0001)
        {
            _logger.LogDebug(
                "Adaptive rate limit for {ModelId}: {Previous:F2} -> {Next:F2} ({Reason}).",
                modelId,
                current,
                next,
                reason);
        }

        _modelFactors[modelId] = new ModelFactor(next, saturation, reason, now);
    }

    /// <summary>
    /// Drops backoff entries for partitions that have stopped being refused, so a client that simply
    /// went away does not hold a table slot for the life of the process.
    /// </summary>
    private void ExpireBackoff(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromSeconds(Math.Max(60, _options.MaxRetryAfterSeconds * 2));
        foreach (var pair in _backoff)
        {
            if (pair.Value.LastRejectionUtc < cutoff &&
                _backoff.TryRemove(new KeyValuePair<string, PartitionBackoff>(pair.Key, pair.Value)))
            {
                Interlocked.Decrement(ref _backoffCount);
            }
        }
    }

    /// <summary>
    /// Trims the table back under <see cref="MaxBackoffPartitions"/>, dropping the partitions that
    /// were refused longest ago.
    /// </summary>
    /// <remarks>
    /// Ordering by last rejection is what makes this safe: a partition actively being refused is
    /// touched on every refusal, so it sorts to the end and cannot be dropped — and therefore
    /// handed a fresh, un-escalated <c>Retry-After</c> — by a flood of new partitions. Running on
    /// the maintenance tick rather than on the request path keeps the O(n log n) sort off the hot
    /// path, which is the same trade the store's partition ceiling makes.
    /// </remarks>
    private void EnforceBackoffCeiling()
    {
        var excess = Volatile.Read(ref _backoffCount) - MaxBackoffPartitions;
        if (excess <= 0)
        {
            return;
        }

        var victims = _backoff
            .ToArray()
            .OrderBy(static pair => pair.Value.LastRejectionUtc)
            .Take(excess);

        foreach (var victim in victims)
        {
            if (_backoff.TryRemove(victim))
            {
                Interlocked.Decrement(ref _backoffCount);
            }
        }
    }

    /// <summary>
    /// Clamps the configured gains into ranges the control law is stable in. A high watermark below
    /// the low one, a decrease factor of 1.0, or a floor above 1.0 would each turn adaptation into
    /// something between a no-op and an oscillator; catching that here means a typo in appsettings
    /// degrades to sane behaviour rather than to a production incident.
    /// </summary>
    private static AdaptiveRateLimitOptions Sanitize(AdaptiveRateLimitOptions options)
    {
        var low = Math.Clamp(options.LowWatermark, 0.05, 0.95);
        var high = Math.Clamp(options.HighWatermark, low + 0.05, 1.5);

        return new AdaptiveRateLimitOptions
        {
            Enabled = options.Enabled,
            MinFactor = Math.Clamp(options.MinFactor, 0.05, 1.0),
            LowWatermark = low,
            HighWatermark = high,
            DecreaseFactor = Math.Clamp(options.DecreaseFactor, 0.1, 0.99),
            IncreaseStep = Math.Clamp(options.IncreaseStep, 0.01, 0.5),
            BackoffAfterConsecutiveRejections = Math.Max(1, options.BackoffAfterConsecutiveRejections),
            BackoffGrowthFactor = Math.Clamp(options.BackoffGrowthFactor, 1.01, 4.0),
            MaxRetryAfterSeconds = Math.Clamp(options.MaxRetryAfterSeconds, 1, 3600),
            RetryAfterJitter = Math.Clamp(options.RetryAfterJitter, 0.0, 0.9),
        };
    }

    private sealed record ModelFactor(double Factor, double Saturation, string Reason, DateTimeOffset UpdatedUtc);

    private sealed class PartitionBackoff
    {
        private int _consecutiveRejections;
        private long _lastRejectionTicks;

        public int ConsecutiveRejections => Volatile.Read(ref _consecutiveRejections);

        public DateTimeOffset LastRejectionUtc => new(Interlocked.Read(ref _lastRejectionTicks), TimeSpan.Zero);

        public void RecordRejection(DateTimeOffset now)
        {
            // Capped so a partition refused for hours cannot overflow the counter, and because the
            // Retry-After ceiling makes every value past the cap indistinguishable anyway.
            if (Volatile.Read(ref _consecutiveRejections) < 1_000)
            {
                Interlocked.Increment(ref _consecutiveRejections);
            }

            Interlocked.Exchange(ref _lastRejectionTicks, now.UtcTicks);
        }
    }
}
