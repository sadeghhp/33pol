using Pol33.Core.Abstractions;

namespace Pol33.Core.Tests.Abstractions;

public sealed class ApiKeyValidatorSubstituteTests
{
    [Fact]
    public void NSubstitute_CanMockCoreInterface()
    {
        var validator = Substitute.For<IApiKeyValidator>();
        validator.Validate("key", Pol33.Core.Security.ApiKeyPolicy.Inference)
            .Returns(Pol33.Core.Security.ApiKeyValidationResult.Success);

        validator.Validate("key", Pol33.Core.Security.ApiKeyPolicy.Inference).IsSuccess.Should().BeTrue();
    }
}
