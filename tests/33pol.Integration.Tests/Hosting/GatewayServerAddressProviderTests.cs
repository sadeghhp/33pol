using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Pol33.App.Hosting;

namespace Pol33.Integration.Tests.Hosting;

/// <summary>
/// The matching rules behind the GW-02 self-probe guard.
/// </summary>
/// <remarks>
/// Both directions matter and they are not symmetric in cost. A missed self-route leaves a backend
/// certifying itself healthy forever, which is the defect. A false positive takes a working upstream
/// out of service, which is worse — so the port is always compared, and an unknown listener never
/// condemns anything.
/// </remarks>
public sealed class GatewayServerAddressProviderTests
{
    [Theory]
    // Kestrel's wildcard forms answer on every interface, so any loopback address is this gateway.
    [InlineData("http://+:8080", "http://localhost:8080")]
    [InlineData("http://+:8080", "http://127.0.0.1:8080")]
    [InlineData("http://*:8080", "http://localhost:8080")]
    [InlineData("http://0.0.0.0:8080", "http://127.0.0.1:8080")]
    [InlineData("http://[::]:8080", "http://localhost:8080")]
    // An explicit bind matches itself, and the loopback spellings of itself.
    [InlineData("http://127.0.0.1:5321", "http://127.0.0.1:5321")]
    [InlineData("http://127.0.0.1:5321", "http://localhost:5321")]
    [InlineData("http://localhost:5321", "http://127.0.0.1:5321")]
    public void IsSelf_RecognisesTheGatewaysOwnListener(string listener, string url) =>
        Provider(listener).IsSelf(url).Should().BeTrue();

    [Theory]
    // A different port on the same machine is an ordinary upstream — Ollama, vLLM, LM Studio.
    [InlineData("http://+:8080", "http://localhost:11434")]
    [InlineData("http://127.0.0.1:5321", "http://127.0.0.1:5322")]
    // A wildcard listener says nothing about remote hosts, and DNS is not resolved on a health sweep.
    [InlineData("http://+:8080", "http://vllm.internal:8080")]
    [InlineData("http://127.0.0.1:8080", "http://10.0.0.5:8080")]
    public void IsSelf_LeavesRealUpstreamsAlone(string listener, string url) =>
        Provider(listener).IsSelf(url).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void IsSelf_UnparseableInput_IsNotSelf(string? url) =>
        Provider("http://+:8080").IsSelf(url).Should().BeFalse();

    /// <summary>
    /// Before the server has bound anything there is no listener to compare against. Guessing here
    /// would condemn backends on a host that simply has not started yet.
    /// </summary>
    [Fact]
    public void IsSelf_BeforeTheServerHasBound_IsNotSelf() =>
        new GatewayServerAddressProvider(new StubServer()).IsSelf("http://localhost:8080")
            .Should().BeFalse();

    /// <summary>A gateway on several endpoints is itself on all of them.</summary>
    [Fact]
    public void IsSelf_MatchesAnyOfSeveralListeners()
    {
        var provider = Provider("http://127.0.0.1:8080", "https://127.0.0.1:8443");

        provider.IsSelf("https://127.0.0.1:8443").Should().BeTrue();
        provider.IsSelf("http://127.0.0.1:8080").Should().BeTrue();
        provider.IsSelf("http://127.0.0.1:9999").Should().BeFalse();
    }

    private static GatewayServerAddressProvider Provider(params string[] addresses) =>
        new(new StubServer(addresses));

    private sealed class StubServer : IServer
    {
        public StubServer(params string[] addresses)
        {
            Features = new FeatureCollection();
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses)
            {
                feature.Addresses.Add(address);
            }

            Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; }

        public void Dispose()
        {
        }

        public Task StartAsync<TContext>(
            IHttpApplication<TContext> application,
            CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
