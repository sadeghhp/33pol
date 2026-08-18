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
using Pol33.Core.Security;
using Pol33.Security.Authentication;
using Pol33.Security.Errors;
using Pol33.Security.Hosting;

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

    /// <summary>
    /// A key matching no stored record is treated as no key at all on a public model.
    /// </summary>
    /// <remarks>
    /// OpenAI-compatible SDKs refuse to construct a client with an empty api_key, so callers of a
    /// public model send a placeholder ("lm-studio", "not-needed", ...). Rejecting those made
    /// publicAccess unreachable from the SDKs it exists to serve, and it buys nothing: the route
    /// already serves callers who send no credential at all.
    /// </remarks>
    [Fact]
    public async Task HandleAuthenticateAsync_PublicInferenceUnrecognizedKey_ReturnsNoResult()
    {
        var handler = CreateHandler(out var authState, out _, FailingValidator(ApiKeyValidationFailure.Invalid));
        authState.IsAuthenticationRequired = true;

        var context = PublicInferenceContext("Bearer lm-studio");

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
        PublicModelAccess.HasRejectedCredential(context).Should().BeFalse();
    }

    /// <summary>
    /// A key the gateway recognises but will not honour still fails, even on a public model.
    /// </summary>
    /// <remarks>
    /// Falling through to anonymous access here answered 200 to a caller whose key had been revoked,
    /// had expired, or whose tenant had been deactivated, so clients, CI checks and SDKs had no way
    /// to discover that their credential had stopped working.
    /// </remarks>
    [Theory]
    [InlineData(ApiKeyValidationFailure.Revoked, "invalid_api_key")]
    [InlineData(ApiKeyValidationFailure.Expired, "expired_api_key")]
    [InlineData(ApiKeyValidationFailure.TenantInactive, "invalid_api_key")]
    public async Task HandleAuthenticateAsync_PublicInferenceRecognizedButUnusableKey_Fails(
        ApiKeyValidationFailure failure,
        string expectedCode)
    {
        var handler = CreateHandler(out var authState, out _, FailingValidator(failure));
        authState.IsAuthenticationRequired = true;

        var context = PublicInferenceContext("Bearer sk-33pol-real-but-dead");

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.None.Should().BeFalse();
        result.Succeeded.Should().BeFalse();
        PublicModelAccess.HasRejectedCredential(context).Should().BeTrue();
        context.Items[GatewayAuthContextItems.AuthFailureCode].Should().Be(expectedCode);
    }

    /// <summary>
    /// The models listing is anonymous-capable too, so a placeholder key must not hide it: an SDK
    /// configured with a dummy key has to be able to discover which models are public.
    /// </summary>
    [Fact]
    public async Task HandleAuthenticateAsync_ModelsListingUnrecognizedKey_ReturnsNoResult()
    {
        var handler = CreateHandler(out var authState, out _, FailingValidator(ApiKeyValidationFailure.Invalid));
        authState.IsAuthenticationRequired = true;

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/models";
        context.Request.Headers.Authorization = "Bearer not-needed";

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
        PublicModelAccess.HasRejectedCredential(context).Should().BeFalse();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ModelsListingRevokedKey_Fails()
    {
        var handler = CreateHandler(out var authState, out _, FailingValidator(ApiKeyValidationFailure.Revoked));
        authState.IsAuthenticationRequired = true;

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/models";
        context.Request.Headers.Authorization = "Bearer sk-33pol-revoked";

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        PublicModelAccess.HasRejectedCredential(context).Should().BeTrue();
    }

    /// <summary>
    /// The placeholder allowance is scoped to routes that serve anonymous callers. On a private
    /// model an unrecognised key is still an authentication failure.
    /// </summary>
    [Fact]
    public async Task HandleAuthenticateAsync_PrivateModelUnrecognizedKey_Fails()
    {
        var handler = CreateHandler(out var authState, out _, FailingValidator(ApiKeyValidationFailure.Invalid));
        authState.IsAuthenticationRequired = true;

        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        context.Request.Headers.Authorization = "Bearer lm-studio";

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        PublicModelAccess.HasRejectedCredential(context).Should().BeTrue();
    }

    /// <summary>
    /// No key at all on a public model still authenticates anonymously — that is the whole point of
    /// <c>publicAccess</c>, and only the rejected-credential case changes.
    /// </summary>
    [Fact]
    public async Task HandleAuthenticateAsync_PublicInferenceNoKey_ReturnsNoResult()
    {
        var handler = CreateHandler(out var authState, out _, Substitute.For<IApiKeyValidator>());
        authState.IsAuthenticationRequired = true;

        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        context.Items[PublicModelAccessKeys.IsPublicInference] = true;

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
        PublicModelAccess.HasRejectedCredential(context).Should().BeFalse();
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

    /// <summary>
    /// Some proxies and SDKs always emit an X-API-Key header, sometimes empty. That must not shadow
    /// a valid bearer token on the same request and turn it into missing_api_key.
    /// </summary>
    [Fact]
    public async Task HandleAuthenticateAsync_BlankXApiKeyHeader_FallsThroughToBearer()
    {
        var validator = Substitute.For<IApiKeyValidator>();
        validator.ValidateAsync("sk-33pol-good", Arg.Any<CancellationToken>())
            .Returns(ApiKeyValidationResult.Success(Guid.NewGuid(), Guid.NewGuid(), "tenant-a", null, null, ApiKeyRole.Inference));
        var handler = CreateHandler(out var authState, out _, validator);
        authState.IsAuthenticationRequired = true;

        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        context.Request.Headers["X-API-Key"] = "   ";
        context.Request.Headers.Authorization = "Bearer sk-33pol-good";

        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ApiKey, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        await validator.Received(1).ValidateAsync("sk-33pol-good", Arg.Any<CancellationToken>());
    }

    private static IApiKeyValidator FailingValidator(ApiKeyValidationFailure failure)
    {
        var validator = Substitute.For<IApiKeyValidator>();
        validator.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiKeyValidationResult.Fail(failure));
        return validator;
    }

    private static DefaultHttpContext PublicInferenceContext(string? authorization)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Items[PublicModelAccessKeys.IsPublicInference] = true;
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
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
