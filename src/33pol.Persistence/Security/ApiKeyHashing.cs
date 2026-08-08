using System.Security.Cryptography;
using System.Text;

namespace Pol33.Persistence.Security;

public static class ApiKeyHashing
{
    /// <summary>Prefix length stored for keys issued by the current code.</summary>
    public const int PrefixLength = 20;

    /// <summary>
    /// Prefix lengths a lookup must probe, newest first. Keys issued before the prefix was widened are
    /// stored with a shorter prefix; omitting those lengths makes every pre-existing key fail to resolve.
    /// </summary>
    private static readonly int[] LookupPrefixLengths = [PrefixLength, 12];

    public static string Hash(string secret, string pepper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(pepper);

        var key = Encoding.UTF8.GetBytes(pepper);
        var payload = Encoding.UTF8.GetBytes(secret.Trim());
        var hash = HMACSHA256.HashData(key, payload);
        return Convert.ToBase64String(hash);
    }

    public static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    public static string CreatePrefix(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        return Truncate(secret.Trim(), PrefixLength);
    }

    /// <summary>
    /// Every prefix under which <paramref name="secret"/> may be stored, newest format first. Callers must
    /// still verify the hash: a prefix match alone never authenticates a key.
    /// </summary>
    public static IReadOnlyList<string> CreateLookupPrefixes(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var trimmed = secret.Trim();
        var prefixes = new List<string>(LookupPrefixLengths.Length);
        foreach (var length in LookupPrefixLengths)
        {
            var candidate = Truncate(trimmed, length);
            if (!prefixes.Contains(candidate, StringComparer.Ordinal))
            {
                prefixes.Add(candidate);
            }
        }

        return prefixes;
    }

    private static string Truncate(string trimmed, int length) =>
        trimmed.Length <= length ? trimmed : trimmed[..length];
}
