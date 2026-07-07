namespace Pol33.Billing.Usage;

/// <summary>
/// In-memory ledger of estimated costs reserved for in-flight requests, so budget enforcement can
/// account for concurrent requests whose actual cost is not yet known (an LLM request's cost is only
/// known once the response returns and is persisted). A reservation is taken before forwarding and
/// released once the request's actual cost has been persisted; a TTL sweep reclaims reservations for
/// requests that never persist usage (e.g. upstream errors) so the ledger cannot leak.
///
/// Single-instance semantics: outstanding reservations are tracked per tenant in this process. A
/// multi-instance deployment would need a shared store.
/// </summary>
public sealed class BudgetReservationLedger
{
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, Reservation> _byRequest = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, decimal> _outstandingByTenant = new();

    public BudgetReservationLedger(TimeSpan ttl, Func<DateTimeOffset>? clock = null)
    {
        _ttl = ttl > TimeSpan.Zero ? ttl : TimeSpan.FromSeconds(120);
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>Current outstanding reservation total for a tenant (expired entries swept first).</summary>
    public decimal GetOutstanding(Guid tenantId)
    {
        lock (_sync)
        {
            SweepExpired();
            return _outstandingByTenant.TryGetValue(tenantId, out var value) ? value : 0m;
        }
    }

    /// <summary>
    /// Atomically reserves <paramref name="amount"/> for <paramref name="requestId"/> if it fits within
    /// <paramref name="headroom"/> (the smallest remaining allowance across the tenant's hard-stop
    /// budgets, already net of persisted spend). Returns false without reserving if it would not fit.
    /// A non-positive amount always succeeds without consuming headroom.
    /// </summary>
    public bool TryReserve(string requestId, Guid tenantId, decimal amount, decimal headroom)
    {
        lock (_sync)
        {
            SweepExpired();

            if (_byRequest.ContainsKey(requestId))
            {
                return true; // idempotent: already reserved for this request
            }

            var outstanding = _outstandingByTenant.TryGetValue(tenantId, out var existing) ? existing : 0m;

            if (amount > 0m && outstanding + amount > headroom)
            {
                return false;
            }

            _byRequest[requestId] = new Reservation(tenantId, amount, _clock() + _ttl);
            if (amount > 0m)
            {
                _outstandingByTenant[tenantId] = outstanding + amount;
            }

            return true;
        }
    }

    /// <summary>Releases the reservation for a request (no-op if unknown). Safe to call more than once.</summary>
    public void Release(string requestId)
    {
        lock (_sync)
        {
            if (!_byRequest.Remove(requestId, out var reservation))
            {
                return;
            }

            Subtract(reservation.TenantId, reservation.Amount);
        }
    }

    private void SweepExpired()
    {
        var now = _clock();
        List<string>? expired = null;
        foreach (var (requestId, reservation) in _byRequest)
        {
            if (reservation.ExpiresAt <= now)
            {
                (expired ??= []).Add(requestId);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var requestId in expired)
        {
            if (_byRequest.Remove(requestId, out var reservation))
            {
                Subtract(reservation.TenantId, reservation.Amount);
            }
        }
    }

    private void Subtract(Guid tenantId, decimal amount)
    {
        if (amount <= 0m || !_outstandingByTenant.TryGetValue(tenantId, out var current))
        {
            return;
        }

        var next = current - amount;
        if (next <= 0m)
        {
            _outstandingByTenant.Remove(tenantId);
        }
        else
        {
            _outstandingByTenant[tenantId] = next;
        }
    }

    private readonly record struct Reservation(Guid TenantId, decimal Amount, DateTimeOffset ExpiresAt);
}
