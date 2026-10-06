namespace Pol33.App;

/// <summary>
/// Recognizes Vite/Rollup content-hashed admin asset file names for cache policy.
/// </summary>
internal static class AdminAssetFileNames
{
    /// <summary>
    /// Rollup/Vite default <c>[hash]</c> length. Hashes may include <c>-</c> and <c>_</c>
    /// (e.g. <c>index-D-zrAnIu.js</c>).
    /// </summary>
    public const int ContentHashLength = 8;

    /// <summary>
    /// True when <paramref name="fileName"/> ends with <c>-{hash}.ext</c> where <c>{hash}</c> is
    /// exactly <see cref="ContentHashLength"/> URL-safe characters.
    /// </summary>
    public static bool HasContentHash(ReadOnlySpan<char> fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < ContentHashLength + 2)
        {
            return false;
        }

        var hashStart = dot - ContentHashLength;
        if (fileName[hashStart - 1] != '-')
        {
            return false;
        }

        for (var i = hashStart; i < dot; i++)
        {
            var c = fileName[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
