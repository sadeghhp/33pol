using System.Net;
using FluentAssertions;
using Pol33.Core.Configuration;

namespace Pol33.Core.Tests.Configuration;

public sealed class GatewayForwardedHeadersOptionsTests
{
    [Fact]
    public void Validate_DefaultOptions_ReturnsNoErrors()
    {
        new GatewayForwardedHeadersOptions().Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_WellFormedTrustAnchors_ReturnsNoErrors()
    {
        var options = new GatewayForwardedHeadersOptions
        {
            Enabled = true,
            KnownProxies = ["10.0.0.7", "2001:db8::1"],
            KnownNetworks = ["10.0.0.0/8", "192.168.0.0/16"],
        };

        options.Validate().Should().BeEmpty();
        options.GetKnownProxies().Should().HaveCount(2);
        options.GetKnownNetworks().Should().HaveCount(2);
    }

    [Fact]
    public void Validate_MalformedProxyAddress_ReportsTheOffendingEntry()
    {
        var options = new GatewayForwardedHeadersOptions { KnownProxies = ["10.0.0.7", "not-an-ip"] };

        options.Validate().Should().ContainSingle()
            .Which.Should().Contain("not-an-ip");
    }

    /// <summary>
    /// A network entry missing its prefix length is the easy mistake, and it would otherwise be
    /// silent — leaving the operator believing a range is trusted when nothing is.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/")]
    [InlineData("garbage")]
    public void Validate_MalformedNetwork_ReportsTheOffendingEntry(string entry)
    {
        var options = new GatewayForwardedHeadersOptions { KnownNetworks = [entry] };

        options.Validate().Should().ContainSingle()
            .Which.Should().Contain(entry);
    }

    /// <summary>
    /// Host bits are masked off rather than rejected, so an operator who writes a host address with
    /// a prefix length still gets the range they meant.
    /// </summary>
    [Fact]
    public void GetKnownNetworks_MasksHostBits()
    {
        var options = new GatewayForwardedHeadersOptions { KnownNetworks = ["10.1.2.3/8"] };

        options.Validate().Should().BeEmpty();
        var network = options.GetKnownNetworks().Should().ContainSingle().Subject;
        network.BaseAddress.Should().Be(IPAddress.Parse("10.0.0.0"));
        network.PrefixLength.Should().Be(8);
        network.Contains(IPAddress.Parse("10.9.9.9")).Should().BeTrue();
    }

    [Fact]
    public void Validate_ForwardLimitBelowOne_ReturnsError()
    {
        var options = new GatewayForwardedHeadersOptions { ForwardLimit = 0 };

        options.Validate().Should().ContainSingle()
            .Which.Should().Contain(nameof(GatewayForwardedHeadersOptions.ForwardLimit));
    }

    [Fact]
    public void GetKnownProxies_TrimsAndDropsBlankEntries()
    {
        var options = new GatewayForwardedHeadersOptions { KnownProxies = ["  10.0.0.7  ", "", "   "] };

        options.Validate().Should().BeEmpty();
        options.GetKnownProxies().Should().ContainSingle()
            .Which.Should().Be(IPAddress.Parse("10.0.0.7"));
    }

    /// <summary>
    /// Enabling the feature without naming a proxy leaves only the framework's loopback default
    /// trusted, so an ingress on another host is ignored and the anonymous tier still shares one
    /// partition. Startup warns about it, which is what this flag drives.
    /// </summary>
    [Fact]
    public void HasNoExplicitTrustAnchors_IsTrueWhenNothingIsNamed()
    {
        new GatewayForwardedHeadersOptions { Enabled = true }
            .HasNoExplicitTrustAnchors.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void HasNoExplicitTrustAnchors_IsFalseWhenAnyTrustIsConfigured(
        bool withProxy,
        bool withNetwork,
        bool trustAll)
    {
        var options = new GatewayForwardedHeadersOptions
        {
            Enabled = true,
            KnownProxies = withProxy ? ["10.0.0.7"] : [],
            KnownNetworks = withNetwork ? ["10.0.0.0/8"] : [],
            TrustAllProxies = trustAll,
        };

        options.HasNoExplicitTrustAnchors.Should().BeFalse();
    }

    [Fact]
    public void GatewayOptionsValidation_SurfacesForwardedHeaderErrors()
    {
        var options = new GatewayOptions
        {
            ForwardedHeaders = new GatewayForwardedHeadersOptions { KnownProxies = ["not-an-ip"] },
        };

        GatewayOptionsValidation.IsValid(options, out var errors).Should().BeFalse();
        errors.Should().ContainSingle().Which.Should().Contain("not-an-ip");
    }
}
