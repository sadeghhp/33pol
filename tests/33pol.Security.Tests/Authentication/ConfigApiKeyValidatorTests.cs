using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.Security;
using Pol33.Security.Authentication;

namespace Pol33.Security.Tests.Authentication;

public sealed class ConfigApiKeyValidatorTests
{
    [Fact]
    public void Validate_NoKeysConfigured_ReturnsSuccess()
    {
        var validator = CreateValidator(new GatewayOptions());

        validator.Validate(null, ApiKeyPolicy.Inference).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidInferenceKey_ReturnsSuccess()
    {
        var validator = CreateValidator(new GatewayOptions { ApiKeys = ["secret-inference"] });

        validator.Validate("secret-inference", ApiKeyPolicy.Inference).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_MissingKey_ReturnsMissing()
    {
        var validator = CreateValidator(new GatewayOptions { ApiKeys = ["secret-inference"] });

        validator.Validate(null, ApiKeyPolicy.Inference).Status.Should().Be(ApiKeyValidationStatus.Missing);
    }

    [Fact]
    public void Validate_InvalidKey_ReturnsInvalid()
    {
        var validator = CreateValidator(new GatewayOptions { ApiKeys = ["secret-inference"] });

        validator.Validate("wrong", ApiKeyPolicy.Inference).Status.Should().Be(ApiKeyValidationStatus.Invalid);
    }

    [Fact]
    public void Validate_AdminKey_OnAdminPolicy_ReturnsSuccess()
    {
        var validator = CreateValidator(new GatewayOptions { AdminApiKeys = ["admin-secret"] });

        validator.Validate("admin-secret", ApiKeyPolicy.Admin).IsSuccess.Should().BeTrue();
    }

    private static ConfigApiKeyValidator CreateValidator(GatewayOptions options) =>
        new(Options.Create(options));
}
