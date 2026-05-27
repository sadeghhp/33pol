using System.Security.Cryptography;
using System.Text;

namespace Pol33.Registry.Services;

internal static class UpstreamSecretFileCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] KeyDerivationSalt = "33pol-upstream-secrets-v1"u8.ToArray();

    internal static byte[] DeriveKey(string pepper)
    {
        var pepperBytes = Encoding.UTF8.GetBytes(pepper ?? string.Empty);
        return SHA256.HashData(KeyDerivationSalt.Concat(pepperBytes).ToArray());
    }

    internal static string Encrypt(string plaintext, byte[] key)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plainBytes, cipher, tag);
        var packed = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, packed, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, packed, NonceSize + cipher.Length, TagSize);
        return Convert.ToBase64String(packed);
    }

    internal static bool TryDecrypt(string ciphertext, byte[] key, out string plaintext)
    {
        plaintext = string.Empty;
        try
        {
            var packed = Convert.FromBase64String(ciphertext);
            if (packed.Length < NonceSize + TagSize)
            {
                return false;
            }

            var cipherLen = packed.Length - NonceSize - TagSize;
            var nonce = packed.AsSpan(0, NonceSize);
            var cipher = packed.AsSpan(NonceSize, cipherLen);
            var tag = packed.AsSpan(NonceSize + cipherLen, TagSize);
            var plain = new byte[cipherLen];
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipher, tag, plain);
            plaintext = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
