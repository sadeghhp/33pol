using Microsoft.Extensions.Hosting;
using NSubstitute;
using Pol33.Security.Configuration;
using Pol33.Security.DependencyInjection;

namespace Pol33.Security.Tests.Configuration;

/// <summary>
/// The API-key/model-grant cache TTL is the gateway's revocation SLA: cache invalidation is
/// in-process only, so on a multi-replica deployment a revoked credential keeps working elsewhere
/// until the entry expires. An unbounded TTL would make that window arbitrarily long.
/// </summary>
public sealed class SecurityCacheTtlValidationTests
{
    private static GatewaySecurityOptionsValidator CreateValidator(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName = environmentName;
        return new GatewaySecurityOptionsValidator(environment);
    }

    private static GatewaySecurityOptions Options(int cacheTtlMinutes) =>
        new()
        {
            CacheTtlMinutes = cacheTtlMinutes,
            KeyPepper = new string('p', 32),
        };

    [Fact]
    public void DefaultTtl_IsAccepted()
    {
        CreateValidator(Environments.Production)
            .Validate(null, new GatewaySecurityOptions { KeyPepper = new string('p', 32) })
            .Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(GatewaySecurityOptions.MaximumCacheTtlMinutes)]
    public void TtlWithinTheBound_IsAccepted(int minutes)
    {
        CreateValidator(Environments.Production)
            .Validate(null, Options(minutes))
            .Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(GatewaySecurityOptions.MaximumCacheTtlMinutes + 1)]
    [InlineData(60)]
    [InlineData(1440)]
    public void TtlBeyondTheBound_IsRejected(int minutes)
    {
        var result = CreateValidator(Environments.Production).Validate(null, Options(minutes));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("CacheTtlMinutes");
        result.FailureMessage.Should().Contain("revoked");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTtl_IsRejected(int minutes)
    {
        CreateValidator(Environments.Production).Validate(null, Options(minutes)).Failed.Should().BeTrue();
    }

    /// <summary>
    /// Unlike the pepper check, the TTL bound applies in Development too: an over-long TTL is a
    /// security problem regardless of environment, and a dev-only escape hatch would let it reach
    /// production configuration unnoticed.
    /// </summary>
    [Fact]
    public void TtlBeyondTheBound_IsRejectedInDevelopmentToo()
    {
        CreateValidator(Environments.Development)
            .Validate(null, Options(GatewaySecurityOptions.MaximumCacheTtlMinutes + 1))
            .Failed.Should().BeTrue();
    }
}
