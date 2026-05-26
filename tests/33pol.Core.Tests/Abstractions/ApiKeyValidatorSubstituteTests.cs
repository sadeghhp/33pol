using Pol33.Core.Abstractions;

namespace Pol33.Core.Tests.Abstractions;

public sealed class ApiKeyValidatorSubstituteTests
{
    [Fact]
    public void NSubstitute_CanMockCoreInterface()
    {
        var validator = Substitute.For<IApiKeyValidator>();

        validator.Should().NotBeNull();
        validator.Should().BeAssignableTo<IApiKeyValidator>();
    }
}
