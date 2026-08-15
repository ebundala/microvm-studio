using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MicroVMStudio.Core.Orchestration;

/// <summary>
/// Encrypts secret values at rest with AES-GCM under a host key file.
///
/// Replaces ShopCare's ASP.NET Data Protection key ring, which assumes a
/// server-side hosting model. The key lives in the user's data directory beside
/// the database: it protects against the database file being copied off the
/// machine, not against an attacker who already has the user's account.
/// </summary>
public sealed class SecretsProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SecretsProtector(IOptions<StudioOptions> options)
    {
        var keyPath = Path.Combine(
            Path.GetDirectoryName(options.Value.DatabasePath)!, "secrets.key");
        _key = LoadOrCreateKey(keyPath);
    }

    public byte[] Protect(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        // nonce || tag || ciphertext
        var result = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        cipher.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    public string Unprotect(byte[] protectedValue)
    {
        if (protectedValue.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Encrypted secret is truncated.");
        }

        var nonce = protectedValue.AsSpan(0, NonceSize);
        var tag = protectedValue.AsSpan(NonceSize, TagSize);
        var cipher = protectedValue.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] LoadOrCreateKey(string keyPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

        if (File.Exists(keyPath)) return File.ReadAllBytes(keyPath);

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyPath, key);
        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
}
