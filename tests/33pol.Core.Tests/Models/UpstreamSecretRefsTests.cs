using FluentAssertions;
using Pol33.Core.Models;

namespace Pol33.Core.Tests.Models;

public sealed class UpstreamSecretRefsTests
{
    [Fact]
    public void ForModel_ReturnsExpectedRef()
    {
        UpstreamSecretRefs.ForModel("my-model").Should().Be("file:model:my-model");
    }

    [Fact]
    public void TryParseModelId_ValidRef_ReturnsId()
    {
        UpstreamSecretRefs.TryParseModelId("file:model:abc", out var id).Should().BeTrue();
        id.Should().Be("abc");
    }

    [Fact]
    public void IsValidForModel_MatchingId_ReturnsTrue()
    {
        UpstreamSecretRefs.IsValidForModel("file:model:abc", "abc").Should().BeTrue();
    }
}
