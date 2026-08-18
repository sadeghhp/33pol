using System.Text;
using Pol33.Core.Billing;

namespace Pol33.Core.Tests.Billing;

public sealed class BillingEventCursorTests
{
    [Fact]
    public void Encode_ThenTryDecode_RoundTrips()
    {
        var at = new DateTimeOffset(2026, 5, 26, 12, 30, 0, TimeSpan.Zero);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        BillingEventCursor.TryDecode(new BillingEventCursor(at, ids).Encode(), out var decoded).Should().BeTrue();

        decoded!.At.Should().Be(at);
        decoded.BoundaryIds.Should().Equal(ids);
    }

    /// <summary>
    /// long.TryParse accepts tick values DateTimeOffset cannot represent; a crafted cursor used to
    /// escape as ArgumentOutOfRangeException from a "Try" method (a 500 at the admin endpoint).
    /// </summary>
    [Theory]
    [InlineData("9223372036854775807")] // long.MaxValue
    [InlineData("3155378976000000000")] // DateTime.MaxValue.Ticks + 1
    public void TryDecode_TicksBeyondDateTimeRange_ReturnsFalseInsteadOfThrowing(string ticks)
    {
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(ticks));

        var act = () => BillingEventCursor.TryDecode(encoded, out _);

        act.Should().NotThrow().Which.Should().BeFalse();
    }

    [Fact]
    public void TryDecode_MaxRepresentableTicks_Succeeds()
    {
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(DateTime.MaxValue.Ticks.ToString()));

        BillingEventCursor.TryDecode(encoded, out var cursor).Should().BeTrue();
        cursor!.At.UtcTicks.Should().Be(DateTime.MaxValue.Ticks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!")]
    [InlineData("LTE=")] // "-1"
    public void TryDecode_Garbage_ReturnsFalse(string encoded)
    {
        BillingEventCursor.TryDecode(encoded, out var cursor).Should().BeFalse();
        cursor.Should().BeNull();
    }
}
