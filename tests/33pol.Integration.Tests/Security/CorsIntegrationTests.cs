using System.Net;
using Microsoft.Extensions.Hosting;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Security;

public sealed class CorsIntegrationTests
{
    private const string AllowedOrigin = "http://localhost:5173";
    private const string DisallowedOrigin = "http://evil.example.com";

    [Fact]
    public async Task Options_Preflight_WithAllowedOrigin_ReturnsAccessControlAllowOrigin()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings =>
            {
                settings["Gateway:Cors:AllowedOrigins:0"] = AllowedOrigin;
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
    }

    [Fact]
    public async Task Options_Preflight_WithDisallowedOrigin_OmitsAllowOrigin()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings =>
            {
                settings["Gateway:Cors:AllowedOrigins:0"] = AllowedOrigin;
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
}
