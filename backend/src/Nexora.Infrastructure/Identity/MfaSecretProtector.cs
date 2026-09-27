using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Nexora.Application.Security;

namespace Nexora.Infrastructure.Identity;

/// <summary>
/// Chiffrement AES-256-GCM du secret TOTP au repos (Phase 47 §17-18). Clé issue de configuration
/// (<c>Security:MfaEncryptionKey</c>, base64 32 octets — même convention que <c>Jwt:SigningKey</c>),
/// avec un repli développement explicitement nommé. Format stocké : base64(nonce ‖ tag ‖ ciphertext).
/// </summary>
public sealed class AesGcmMfaSecretProtector : IMfaSecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmMfaSecretProtector(IConfiguration configuration)
    {
        var configured = configuration["Security:MfaEncryptionKey"];
        _key = string.IsNullOrWhiteSpace(configured)
            ? SHA256.HashData("dev-only-insecure-mfa-key-change-me-0123456789"u8.ToArray())
            : Convert.FromBase64String(configured);
        if (_key.Length != 32)
            throw new InvalidOperationException("Security:MfaEncryptionKey doit décoder en 32 octets (AES-256).");
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var packed = new byte[NonceSize + TagSize + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, packed, NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, packed, NonceSize + TagSize, cipherBytes.Length);
        return Convert.ToBase64String(packed);
    }

    public string Unprotect(string ciphertext)
    {
        var packed = Convert.FromBase64String(ciphertext);
        var nonce = packed[..NonceSize];
        var tag = packed[NonceSize..(NonceSize + TagSize)];
        var cipherBytes = packed[(NonceSize + TagSize)..];
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return System.Text.Encoding.UTF8.GetString(plainBytes);
    }
}
