using FluentAssertions;
using Pol33.Core.Configuration;

namespace Pol33.Core.Tests.Configuration;

public sealed class CorsConfigValidationTests
{
    [Fact]
    public void TryValidate_ValidHttpsAndHttpOrigins_ReturnsNormalized()
    {
        var input = new[]
        {
            "https://sadeghhp.github.io/",
            "http://localhost:5173",
            "  https://sadeghhp.github.io  ",
        };

        CorsConfigValidation.TryValidate(input, out var error, out var normalized)
            .Should()
            .BeTrue();
        error.Should().BeNull();
        normalized.Should().Equal(
            "https://sadeghhp.github.io",
            "http://localhost:5173");
    }

    [Fact]
    public void TryValidate_Null_ReturnsFalse()
    {
        CorsConfigValidation.TryValidate(null, out var error, out var normalized)
            .Should()
            .BeFalse();
        error.Should().Contain("allowedOrigins");
        normalized.Should().BeEmpty();
    }

    [Fact]
    public void TryValidate_Wildcard_ReturnsFalse()
    {
        CorsConfigValidation.TryValidate(["*"], out var error, out _)
            .Should()
            .BeFalse();
        error.Should().Contain("Wildcard");
    }

    [Fact]
    public void TryValidate_GithubPagesWildcard_ReturnsNormalized()
    {
        CorsConfigValidation.TryValidate(["https://*.github.io/"], out var error, out var normalized)
            .Should()
            .BeTrue();
        error.Should().BeNull();
        normalized.Should().Equal("https://*.github.io");
    }

    [Fact]
    public void TryValidate_InvalidWildcardPattern_ReturnsFalse()
    {
        CorsConfigValidation.TryValidate(["https://*"], out var error, out _)
            .Should()
            .BeFalse();
        error.Should().Contain("subdomain pattern");
    }

    [Fact]
    public void TryValidate_Path_ReturnsFalse()
    {
        CorsConfigValidation.TryValidate(["https://app.example.com/admin"], out var error, out _)
            .Should()
            .BeFalse();
        error.Should().Contain("path");
    }

    [Fact]
    public void TryValidate_Query_ReturnsFalse()
    {
        CorsConfigValidation.TryValidate(["https://app.example.com?x=1"], out var error, out _)
            .Should()
            .BeFalse();
        error.Should().Contain("query");
    }

    [Fact]
    public void TryValidate_NonHttpScheme_ReturnsFalse()
    {
        CorsConfigValidation.TryValidate(["ftp://files.example.com"], out var error, out _)
            .Should()
            .BeFalse();
        error.Should().Contain("http or https");
    }

    [Fact]
    public void TryValidate_EmptyList_ReturnsTrue()
    {
        CorsConfigValidation.TryValidate([], out var error, out var normalized)
            .Should()
            .BeTrue();
        error.Should().BeNull();
        normalized.Should().BeEmpty();
    }

    [Fact]
    public void TryValidate_BlankEntries_AreDropped()
    {
        CorsConfigValidation.TryValidate(["", "  ", "https://ok.example.com"], out var error, out var normalized)
            .Should()
            .BeTrue();
        error.Should().BeNull();
        normalized.Should().Equal("https://ok.example.com");
    }
}
