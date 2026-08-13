using Pol33.Core.Models;

namespace Pol33.Core.Tests.Models;

public sealed class GatewayErrorQueryTests
{
    [Fact]
    public void Clamp_BoundsTheLimit()
    {
        new GatewayErrorQuery { Limit = 99_999 }.Clamp().Limit.Should().Be(GatewayErrorQuery.MaxLimit);
        new GatewayErrorQuery { Limit = 0 }.Clamp().Limit.Should().Be(GatewayErrorQuery.DefaultLimit);
        new GatewayErrorQuery { Limit = -5 }.Clamp().Limit.Should().Be(GatewayErrorQuery.DefaultLimit);
    }

    [Fact]
    public void Clamp_RaisesTheCeilingForExport()
    {
        new GatewayErrorQuery { Limit = 99_999 }
            .Clamp(GatewayErrorQuery.MaxExportLimit)
            .Limit.Should().Be(GatewayErrorQuery.MaxExportLimit);
    }

    [Fact]
    public void Clamp_BoundsTheOffset()
    {
        new GatewayErrorQuery { Offset = -10 }.Clamp().Offset.Should().Be(0);
        new GatewayErrorQuery { Offset = 500_000 }.Clamp().Offset.Should().Be(GatewayErrorQuery.MaxOffset);
    }

    [Fact]
    public void Clamp_SwapsAnInvertedWindow()
    {
        var from = DateTimeOffset.UnixEpoch.AddDays(10);
        var to = DateTimeOffset.UnixEpoch;

        // An inverted range is a user slip, not a request for zero rows.
        var clamped = new GatewayErrorQuery { From = from, To = to }.Clamp();

        clamped.From.Should().Be(to);
        clamped.To.Should().Be(from);
    }

    [Fact]
    public void Clamp_TrimsAndBoundsTheSearchTerm()
    {
        new GatewayErrorQuery { Search = "  timeout  " }.Clamp().Search.Should().Be("timeout");
        new GatewayErrorQuery { Search = "   " }.Clamp().Search.Should().BeNull();
        new GatewayErrorQuery { Search = new string('x', 5000) }
            .Clamp().Search!.Length.Should().Be(GatewayErrorQuery.MaxSearchLength);
    }

    [Fact]
    public void Clamp_NormalizesBlankFiltersToNull()
    {
        var clamped = new GatewayErrorQuery
        {
            ModelId = "  ",
            EventCode = string.Empty,
            TenantId = " tenant ",
        }.Clamp();

        clamped.ModelId.Should().BeNull();
        clamped.EventCode.Should().BeNull();
        clamped.TenantId.Should().Be("tenant");
    }

    [Theory]
    [InlineData("count", GatewayErrorSort.Count)]
    [InlineData("firstSeen", GatewayErrorSort.FirstSeen)]
    [InlineData("lastSeen", GatewayErrorSort.LastSeen)]
    [InlineData("nonsense", GatewayErrorSort.LastSeen)]
    [InlineData(null, GatewayErrorSort.LastSeen)]
    public void ParseSort_FallsBackToNewestFirst(string? input, GatewayErrorSort expected)
    {
        GatewayErrorQuery.ParseSort(input).Should().Be(expected);
    }
}
