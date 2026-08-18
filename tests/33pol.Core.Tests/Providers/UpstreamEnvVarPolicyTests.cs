using FluentAssertions;
using Pol33.Core.Providers;

namespace Pol33.Core.Tests.Providers;

/// <summary>
/// The policy exists because an admin supplies both the variable to read and the URL the resulting
/// token is sent to. Without it, "admin on the gateway" implied "read any secret in the gateway's
/// environment and forward it anywhere".
/// </summary>
public sealed class UpstreamEnvVarPolicyTests
{
    [Theory]
    [InlineData("OPENROUTER_API_KEY")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("MY_VLLM_API_KEY")]
    [InlineData("selfhosted_token")]
    public void IsAllowed_UpstreamCredentialNames_AreAccepted(string envVar)
    {
        new UpstreamEnvVarPolicy().IsAllowed(envVar, out var error).Should().BeTrue(error);
    }

    [Theory]
    [InlineData("GATEWAY_KEY_PEPPER")]
    [InlineData("GATEWAY_ADMIN_API_KEY")]
    [InlineData("POL33_ADMIN_TOKEN")]
    [InlineData("DB_PASSWORD")]
    [InlineData("ConnectionStrings__GatewayDb")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("AWS_SESSION_TOKEN")]
    [InlineData("HOME")]
    [InlineData("STRIPE_SECRET_KEY")]
    [InlineData("JWT_SIGNING_KEY")]
    [InlineData("ENCRYPTION_KEY")]
    [InlineData("AZURE_STORAGE_KEY")]
    [InlineData("SSH_KEY")]
    [InlineData("MASTER_KEY")]
    [InlineData("SSH_PRIVATE_KEY")]
    [InlineData("MY_SECRET_TOKEN")]
    [InlineData("SIGNING_TOKEN")]
    public void IsAllowed_GatewayAndHostSecrets_AreRefused(string envVar)
    {
        var policy = new UpstreamEnvVarPolicy();

        policy.IsAllowed(envVar, out var error).Should().BeFalse();
        error.Should().Contain(UpstreamEnvVarPolicy.AllowListSettingKey);
    }

    [Fact]
    public void IsAllowed_ExplicitlyAllowListedName_IsAccepted()
    {
        var policy = new UpstreamEnvVarPolicy(["MY_ODD_UPSTREAM_CRED"]);

        policy.IsAllowed("MY_ODD_UPSTREAM_CRED", out _).Should().BeTrue();
        policy.IsAllowed("my_odd_upstream_cred", out _).Should().BeTrue("names are matched case-insensitively");
    }

    /// <summary>
    /// A bare *_KEY suffix used to be accepted, which matched a large family of unrelated host
    /// secrets. Such names now need the explicit allow-list.
    /// </summary>
    [Fact]
    public void IsAllowed_BareKeySuffix_RequiresExplicitAllowList()
    {
        new UpstreamEnvVarPolicy().IsAllowed("MY_UPSTREAM_KEY", out var error).Should().BeFalse();
        error.Should().Contain(UpstreamEnvVarPolicy.AllowListSettingKey);

        new UpstreamEnvVarPolicy(["MY_UPSTREAM_KEY"]).IsAllowed("MY_UPSTREAM_KEY", out _).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_EmptyOrMissingName_IsRefused()
    {
        new UpstreamEnvVarPolicy().IsAllowed("  ", out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }
}
