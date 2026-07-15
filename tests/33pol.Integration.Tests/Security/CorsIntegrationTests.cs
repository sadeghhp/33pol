using System.Net;
using Microsoft.Extensions.Hosting;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Security;

public sealed class CorsIntegrationTests
{
    private const string AllowedOrigin = "http://localhost:5173";
    private const string DisallowedOrigin = "http://evil.example.com";
    private const string GitHubPagesOrigin = "https://sadeghhp.github.io";
    private const string GitHubPagesWildcard = "https://*.github.io";

    [Fact]
    public async Task Options_Preflight_WithAllowedOrigin_ReturnsAccessControlAllowOrigin()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings =>
            {
                settings["Gateway:Cors:AllowedOrigins:0"] = AllowedOrigin;
                // No database is configured in this CORS-only test; opt into anonymous mode so the
                // Production host starts (default behavior now fails closed).
                settings["Gateway:Security:AllowAnonymous"] = "true";
            });

        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/chat/completions");
        request.Headers.TryAddWithoutValidation("Origin", AllowedOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "authorization,content-type");

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(AllowedOrigin);
        response.Headers.GetValues("Access-Control-Max-Age").Single().Should().Be("86400");
    }

    [Fact]
    public async Task Options_Preflight_WithDisallowedOrigin_OmitsAllowOrigin()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings =>
            {
                settings["Gateway:Cors:AllowedOrigins:0"] = AllowedOrigin;
                // No database is configured in this CORS-only test; opt into anonymous mode so the
                // Production host starts (default behavior now fails closed).
                settings["Gateway:Security:AllowAnonymous"] = "true";
            });

        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/chat/completions");
        request.Headers.TryAddWithoutValidation("Origin", DisallowedOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "authorization,content-type");

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Options_Preflight_InDevelopment_AllowsAnyOrigin()
    {
        await using var factory = GatewayWebApplicationFactory.Create();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/chat/completions");
        request.Headers.TryAddWithoutValidation("Origin", DisallowedOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "authorization,content-type");

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be("*");
    }

    [Fact]
    public async Task GetModels_WithGithubPagesWildcard_ReturnsAccessControlAllowOrigin()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings =>
            {
                settings["Gateway:Cors:AllowedOrigins:0"] = GitHubPagesWildcard;
                settings["Gateway:Security:AllowAnonymous"] = "true";
            });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Origin", GitHubPagesOrigin);

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(GitHubPagesOrigin);
    }

    [Fact]
    public async Task Options_Models_WithGithubPagesWildcard_ReturnsAccessControlAllowOrigin()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings =>
            {
                settings["Gateway:Cors:AllowedOrigins:0"] = GitHubPagesWildcard;
                settings["Gateway:Security:AllowAnonymous"] = "true";
            });

        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/models");
        request.Headers.TryAddWithoutValidation("Origin", GitHubPagesOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        request.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "authorization,content-type");

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(GitHubPagesOrigin);
        response.Headers.GetValues("Access-Control-Max-Age").Single().Should().Be("86400");
    }

    [Fact]
    public async Task Options_Models_WithDisallowedGithubPagesWildcardOrigin_OmitsAllowOrigin()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings =>
            {
                settings["Gateway:Cors:AllowedOrigins:0"] = GitHubPagesWildcard;
                settings["Gateway:Security:AllowAnonymous"] = "true";
            });

        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/models");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.github.io.evil.com");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        request.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "authorization,content-type");

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task GetModels_WithAllowedOrigin_ReturnsAccessControlAllowOrigin()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings =>
            {
                settings["Gateway:Cors:AllowedOrigins:0"] = GitHubPagesOrigin;
                settings["Gateway:Security:AllowAnonymous"] = "true";
            });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Origin", GitHubPagesOrigin);

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(GitHubPagesOrigin);
    }

    [Fact]
    public async Task Options_Models_WithAllowedOrigin_ReturnsAccessControlAllowOrigin()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings =>
            {
                settings["Gateway:Cors:AllowedOrigins:0"] = GitHubPagesOrigin;
                settings["Gateway:Security:AllowAnonymous"] = "true";
            });

        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/models");
        request.Headers.TryAddWithoutValidation("Origin", GitHubPagesOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        request.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "authorization,content-type");

        var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(GitHubPagesOrigin);
        response.Headers.GetValues("Access-Control-Max-Age").Single().Should().Be("86400");
    }
}
