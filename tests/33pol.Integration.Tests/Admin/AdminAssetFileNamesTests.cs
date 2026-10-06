using Pol33.App;

namespace Pol33.Integration.Tests.Admin;

public sealed class AdminAssetFileNamesTests
{
    [Theory]
    [InlineData("index-D-zrAnIu.js")]
    [InlineData("index-0GWrmJOJ.css")]
    [InlineData("filters-2_tf_mIj.js")]
    [InlineData("IBMPlexSans-400-IvpUvPa2.woff2")]
    [InlineData("ratelimits-BSO3tJlN.js")]
    public void HasContentHash_ViteOutput_ReturnsTrue(string fileName)
    {
        AdminAssetFileNames.HasContentHash(fileName).Should().BeTrue();
    }

    [Theory]
    [InlineData("admin-app.js")]
    [InlineData("fonts.css")]
    [InlineData("index.html")]
    [InlineData("alpine-csp-3.14.9.min.js")]
    [InlineData("index-short.js")]
    public void HasContentHash_UnversionedOrVendorSemver_ReturnsFalse(string fileName)
    {
        AdminAssetFileNames.HasContentHash(fileName).Should().BeFalse();
    }
}
