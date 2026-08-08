using Pol33.Persistence.Security;

namespace Pol33.Persistence.Tests.Security;

public sealed class ApiKeyHashingTests
{
    private const string LongSecret = "sk-33pol-797a8b0b67b157d4d26dd186e5cc2c84";

    [Fact]
    public void CreatePrefix_SecretLongerThanPrefix_TruncatesToPrefixLength()
    {
        var prefix = ApiKeyHashing.CreatePrefix(LongSecret);

        prefix.Should().Be(LongSecret[..ApiKeyHashing.PrefixLength]);
    }

    [Fact]
    public void CreatePrefix_SecretShorterThanPrefix_ReturnsWholeSecret()
    {
        ApiKeyHashing.CreatePrefix("sk-short").Should().Be("sk-short");
    }

    [Fact]
    public void CreateLookupPrefixes_LongSecret_IncludesCurrentAndLegacyLengths()
    {
        var prefixes = ApiKeyHashing.CreateLookupPrefixes(LongSecret);

        prefixes.Should().Equal(LongSecret[..ApiKeyHashing.PrefixLength], LongSecret[..12]);
    }

    [Fact]
    public void CreateLookupPrefixes_ShortSecret_DeduplicatesIdenticalPrefixes()
    {
        var prefixes = ApiKeyHashing.CreateLookupPrefixes("sk-33pol-abc");

        prefixes.Should().Equal("sk-33pol-abc");
    }

    [Fact]
    public void CreateLookupPrefixes_UntrimmedSecret_IgnoresSurroundingWhitespace()
    {
        ApiKeyHashing.CreateLookupPrefixes($"  {LongSecret}  ")
            .Should().Equal(ApiKeyHashing.CreateLookupPrefixes(LongSecret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateLookupPrefixes_MissingSecret_Throws(string? secret)
    {
        Assert.ThrowsAny<ArgumentException>(() => ApiKeyHashing.CreateLookupPrefixes(secret!));
    }

    [Fact]
    public void Hash_SamePepperAndSecret_IsStable()
    {
        ApiKeyHashing.Hash(LongSecret, "pepper").Should().Be(ApiKeyHashing.Hash(LongSecret, "pepper"));
    }

    [Fact]
    public void Hash_DifferentPepper_ProducesDifferentHash()
    {
        ApiKeyHashing.Hash(LongSecret, "pepper-a").Should().NotBe(ApiKeyHashing.Hash(LongSecret, "pepper-b"));
    }
}
