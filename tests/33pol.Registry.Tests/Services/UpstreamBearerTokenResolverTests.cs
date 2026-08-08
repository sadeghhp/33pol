using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.Providers;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class UpstreamBearerTokenResolverTests
{
    [Fact]
    public void ResolveBearerToken_NullAuth_ReturnsNull()
    {
        var resolver = CreateResolver();

        resolver.ResolveBearerToken(null).Should().BeNull();
    }

    [Fact]
    public void ResolveBearerToken_NonBearerType_ReturnsNull()
    {
        var resolver = CreateResolver();

        resolver.ResolveBearerToken(new UpstreamAuthConfig { Type = "basic", EnvVar = "OPENAI_API_KEY" })
            .Should().BeNull();
    }

    [Fact]
    public void ResolveBearerToken_EmptyEnvVar_ReturnsNull()
    {
        var resolver = CreateResolver();

        resolver.ResolveBearerToken(new UpstreamAuthConfig { Type = "bearer", EnvVar = "  " })
            .Should().BeNull();
    }

    [Fact]
    public void ResolveBearerToken_DeniedEnvVar_ReturnsNull()
    {
        var resolver = CreateResolver();

        resolver.ResolveBearerToken(new UpstreamAuthConfig
            {
                Type = "bearer",
                EnvVar = "Gateway__Security__KeyPepper",
            })
            .Should().BeNull();
    }

    [Fact]
    public void ResolveBearerToken_SecretRef_ReturnsStoredSecret()
    {
        var secrets = Substitute.For<IUpstreamSecretStore>();
        secrets.TryGet("m1", out Arg.Any<string?>())
            .Returns(x =>
            {
                x[1] = "sk-from-store";
                return true;
            });

        var resolver = CreateResolver(secrets);

        resolver.ResolveBearerToken(new UpstreamAuthConfig
            {
                Type = "bearer",
                SecretRef = UpstreamSecretRefs.ForModel("m1"),
            })
            .Should().Be("sk-from-store");
    }

    [Fact]
    public void ResolveBearerToken_AllowedEnvVar_ReadsConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "sk-from-config",
            })
            .Build();

        var resolver = CreateResolver(configuration: config);

        resolver.ResolveBearerToken(new UpstreamAuthConfig
            {
                Type = "bearer",
                EnvVar = "OPENAI_API_KEY",
            })
            .Should().Be("sk-from-config");
    }

    private static UpstreamBearerTokenResolver CreateResolver(
        IUpstreamSecretStore? secretStore = null,
        IConfiguration? configuration = null)
    {
        return new UpstreamBearerTokenResolver(
            secretStore ?? Substitute.For<IUpstreamSecretStore>(),
            new UpstreamEnvVarPolicy(),
            configuration ?? new ConfigurationBuilder().Build(),
            NullLogger<UpstreamBearerTokenResolver>.Instance);
    }
}
