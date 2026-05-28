using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Errors;
using Pol33.Security.Authentication;
using Pol33.Security.Errors;

namespace Pol33.Security.Tests.Authentication;

public sealed class ApiKeyAuthenticationHandlerTests
{
    [Fact]
    public async Task HandleAuthenticateAsync_PublicInferenceMissingKey_ReturnsNoResult()
    {
        var handler = CreateHandler(out var authState, out _);
        authState.IsAuthenticationRequired = true;

        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        context.Items[PublicModelAccessKeys.IsPublicInference] = true;

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_PublicInferenceInvalidKey_ReturnsNoResult()
    {
        var validator = Substitute.For<IApiKeyValidator>();
        validator.ValidateAsync("sk-garbage", Arg.Any<CancellationToken>())
            .Returns(ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Invalid));

        var handler = CreateHandler(out var authState, out _, validator);
        authState.IsAuthenticationRequired = true;

        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        context.Items[PublicModelAccessKeys.IsPublicInference] = true;
        context.Request.Headers.Authorization = "Bearer sk-garbage";

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_PrivateModelMissingKey_ReturnsFail()
    {
        var handler = CreateHandler(out var authState, out _);
        authState.IsAuthenticationRequired = true;

        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_GetModelsWithoutKey_ReturnsNoResult()
    {
        var handler = CreateHandler(out var authState, out _);
        authState.IsAuthenticationRequired = true;

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/models";

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    private static ApiKeyAuthenticationHandler CreateHandler(
        out GatewayAuthenticationState authState,
        out IErrorResponseWriter errors,
        IApiKeyValidator? validator = null)
    {
        authState = new GatewayAuthenticationState();
        errors = new OpenAiErrorResponseWriter();
        validator ??= Substitute.For<IApiKeyValidator>();

        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        return new ApiKeyAuthenticationHandler(
            options,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            validator,
            authState,
            errors);
    }
}
