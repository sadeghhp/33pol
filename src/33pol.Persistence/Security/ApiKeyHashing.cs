using System.Security.Cryptography;
using System.Text;

namespace Pol33.Persistence.Security;

public static class ApiKeyHashing
{
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

        const int visibleLength = 12;
        var trimmed = secret.Trim();
        return trimmed.Length <= visibleLength
            ? trimmed
            : trimmed[..visibleLength];
    }
}
