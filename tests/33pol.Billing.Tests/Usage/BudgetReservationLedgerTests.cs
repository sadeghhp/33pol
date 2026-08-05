using Pol33.Billing.Usage;

namespace Pol33.Billing.Tests.Usage;

public sealed class BudgetReservationLedgerTests
{
    [Fact]
    public void TryReserve_WithinHeadroom_Succeeds_AndTracksOutstanding()
    {
        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(2));
        var tenant = Guid.NewGuid();

        ledger.TryReserve("req-1", tenant, amount: 30m, headroom: 100m).Should().BeTrue();
        ledger.GetOutstanding(tenant).Should().Be(30m);
    }

    [Fact]
    public void TryReserve_ConcurrentReservationsExceedingHeadroom_SecondRejected()
    {
        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(2));
        var tenant = Guid.NewGuid();

        // Two in-flight requests: the first fits, the second would push outstanding past headroom.
        ledger.TryReserve("req-1", tenant, amount: 70m, headroom: 100m).Should().BeTrue();
        ledger.TryReserve("req-2", tenant, amount: 70m, headroom: 100m).Should().BeFalse();

        ledger.GetOutstanding(tenant).Should().Be(70m);
    }

    [Fact]
    public void Release_FreesHeadroomForSubsequentReservations()
    {
        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(2));
        var tenant = Guid.NewGuid();

        ledger.TryReserve("req-1", tenant, amount: 70m, headroom: 100m).Should().BeTrue();
        ledger.Release("req-1");
        ledger.GetOutstanding(tenant).Should().Be(0m);

        ledger.TryReserve("req-2", tenant, amount: 70m, headroom: 100m).Should().BeTrue();
    }

    /// <summary>
    /// The router releases on every terminal path and usage persistence releases on settlement, so
    /// the two can race. A double release must not drive outstanding negative or free headroom that
    /// belongs to another in-flight request.
    /// </summary>
    [Fact]
    public void Release_CalledRepeatedly_DoesNotCorruptOutstanding()
    {
        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(2));
        var tenant = Guid.NewGuid();

        ledger.TryReserve("req-1", tenant, amount: 40m, headroom: 100m).Should().BeTrue();
        ledger.TryReserve("req-2", tenant, amount: 40m, headroom: 100m).Should().BeTrue();
        ledger.GetOutstanding(tenant).Should().Be(80m);

        ledger.Release("req-1");
        ledger.Release("req-1");
        ledger.Release("req-1");

        // Only req-1's reservation is freed, however many times release is called.
        ledger.GetOutstanding(tenant).Should().Be(40m);
    }

    [Fact]
    public void Release_UnknownRequestId_IsANoOp()
    {
        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(2));
        var tenant = Guid.NewGuid();

        ledger.TryReserve("req-1", tenant, amount: 25m, headroom: 100m).Should().BeTrue();

        ledger.Release("never-reserved");

        ledger.GetOutstanding(tenant).Should().Be(25m);
    }

    /// <summary>
    /// The complement of the TTL policy: while a request is still in flight (TTL not elapsed) its
    /// reservation must keep holding headroom, so a concurrent request cannot be admitted against
    /// budget that is already spoken for.
    /// </summary>
    [Fact]
    public void Reservation_DoesNotExpireWhileTheRequestIsStillWithinItsTtl()
    {
        var now = DateTimeOffset.UtcNow;
        var ledger = new BudgetReservationLedger(TimeSpan.FromSeconds(600), () => now);
        var tenant = Guid.NewGuid();

        ledger.TryReserve("long-running", tenant, amount: 70m, headroom: 100m).Should().BeTrue();

        // Five minutes into a long streaming generation, well inside the TTL.
        now = now.AddSeconds(300);

        ledger.GetOutstanding(tenant).Should().Be(70m);
        ledger.TryReserve("concurrent", tenant, amount: 70m, headroom: 100m)
            .Should()
            .BeFalse("the in-flight reservation still holds the headroom");
    }

    [Fact]
    public void TryReserve_SameRequestIdTwice_IsIdempotent()
    {
        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(2));
        var tenant = Guid.NewGuid();

        ledger.TryReserve("req-1", tenant, amount: 30m, headroom: 100m).Should().BeTrue();
        ledger.TryReserve("req-1", tenant, amount: 30m, headroom: 100m).Should().BeTrue();

        ledger.GetOutstanding(tenant).Should().Be(30m); // not double-counted
    }

    [Fact]
    public void ExpiredReservations_AreSweptSoLeakedRequestsSelfHeal()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ledger = new BudgetReservationLedger(TimeSpan.FromSeconds(60), () => now);
        var tenant = Guid.NewGuid();

        ledger.TryReserve("req-orphan", tenant, amount: 90m, headroom: 100m).Should().BeTrue();

        now = now.AddSeconds(61); // reservation TTL elapsed without a Release (e.g. upstream error)

        // The stale reservation is reclaimed, so a new request can reserve again.
        ledger.GetOutstanding(tenant).Should().Be(0m);
        ledger.TryReserve("req-new", tenant, amount: 90m, headroom: 100m).Should().BeTrue();
    }

    [Fact]
    public void TryReserve_ZeroAmount_AlwaysSucceeds_WithoutConsumingHeadroom()
    {
        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(2));
        var tenant = Guid.NewGuid();

        // No rate card / unpriced model => zero estimate must never block.
        ledger.TryReserve("req-1", tenant, amount: 0m, headroom: 0m).Should().BeTrue();
        ledger.GetOutstanding(tenant).Should().Be(0m);
    }
}
