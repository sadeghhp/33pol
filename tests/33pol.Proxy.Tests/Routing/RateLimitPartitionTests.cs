using System.Net;
using Microsoft.AspNetCore.Http;
using Pol33.Core.Identity;
using Pol33.Core.Security;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Tests.Routing;

/// <summary>
/// What a limit is counted against. The interesting cases are all about anonymous traffic, where
/// there is no credential and the address is the only thing left to partition on.
/// </summary>
public sealed class RateLimitPartitionTests
{
    [Fact]
    public void Resolve_AuthenticatedRequest_PartitionsByTenant()
    {
        var context = new DefaultHttpContext();
        context.Items[TenantContextKeys.HttpContextItemKey] = new TenantContext
        {
            TenantId = "acme",
            ApiKeyId = "key-1",
            PlanSlug = "pro",
            Role = ApiKeyRole.Inference,
        };

        var subject = RateLimitPartition.ResolveSubject(context);

        subject.PartitionKey.Should().Be("acme");
        subject.TenantId.Should().Be("acme");
        subject.ApiKeyId.Should().Be("key-1");
        subject.PlanSlug.Should().Be("pro");
    }

    [Fact]
    public void ResolveSubject_AnonymousRequest_HasNoIdentityBeyondTheAddress()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        var subject = RateLimitPartition.ResolveSubject(context);

        subject.PartitionKey.Should().Be("anon:203.0.113.7");
        subject.TenantId.Should().BeNull();
        subject.ApiKeyId.Should().BeNull();
    }

    /// <summary>
    /// IPv4 keys on the full address: it is one client, and collapsing it further would make
    /// neighbours share a bucket.
    /// </summary>
    [Fact]
    public void Normalize_IPv4_KeepsTheWholeAddress()
    {
        RateLimitPartition.Normalize(IPAddress.Parse("198.51.100.42")).Should().Be("198.51.100.42");
    }

    /// <summary>
    /// An IPv6 client is routinely handed a /64 or shorter. Keyed on the full 128-bit address it can
    /// mint 2^64 partitions at will — each with its own full bucket, so the limit never binds, and
    /// the churn walks the partition table into its ceiling and resets the buckets of legitimate
    /// callers on the way.
    /// </summary>
    [Theory]
    [InlineData("2001:db8::1")]
    [InlineData("2001:db8::dead:beef")]
    [InlineData("2001:db8:0:0:ffff:ffff:ffff:ffff")]
    public void Normalize_EveryAddressInAnIPv6Block_ProducesOneKey(string address)
    {
        RateLimitPartition.Normalize(IPAddress.Parse(address)).Should().Be("2001:db8::/64");
    }

    [Fact]
    public void Normalize_DifferentIPv6Blocks_StayDistinct()
    {
        RateLimitPartition.Normalize(IPAddress.Parse("2001:db8:0:1::1")).Should()
            .NotBe(RateLimitPartition.Normalize(IPAddress.Parse("2001:db8:0:2::1")));
    }

    /// <summary>
    /// A dual-stack socket reports an IPv4 client as ::ffff:a.b.c.d. Left alone, the same client
    /// would land in a different bucket depending on how the listener happened to be bound.
    /// </summary>
    [Fact]
    public void Normalize_IPv4MappedToIPv6_MatchesThePlainIPv4Key()
    {
        RateLimitPartition.Normalize(IPAddress.Parse("::ffff:198.51.100.42")).Should()
            .Be(RateLimitPartition.Normalize(IPAddress.Parse("198.51.100.42")));
    }

    /// <summary>
    /// The auth-failure budget is namespaced away from the public-model one, so a flood of bad
    /// credentials from an address cannot also exhaust that address's allowance for public models.
    /// </summary>
    [Fact]
    public void ResolveAuthFailure_IsANamespaceOfItsOwn()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        RateLimitPartition.ResolveAuthFailure(context).Should().Be("authfail:203.0.113.7");
        RateLimitPartition.ResolveAuthFailure(context).Should()
            .NotBe(RateLimitPartition.Resolve(context));
    }

    /// <summary>In-memory test servers and unix sockets have no remote address; they must still key on something.</summary>
    [Fact]
    public void Resolve_WithNoRemoteAddress_FallsBackToAKnownKey()
    {
        RateLimitPartition.Resolve(new DefaultHttpContext()).Should()
            .Be(RateLimitPartition.UnknownAnonymousKey);
    }
}
