using System.Security.Cryptography;
using System.Text;

namespace Nexora.Infrastructure.Identity;

/// <summary>
/// Générateur / vérificateur TOTP (RFC 6238, HMAC-SHA1, 6 chiffres, pas de 30 s) et
/// encodage Base32 (RFC 4648) pour le secret partagé — compatible Google Authenticator,
/// Aegis, 1Password, etc. (Phase 33 §8). Aucune dépendance externe.
/// </summary>
public static class Totp
{
    private const int Digits = 6;
    private const int PeriodSeconds = 30;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Nouveau secret aléatoire (160 bits) encodé en Base32.</summary>
    public static string NewSecret() => ToBase32(RandomNumberGenerator.GetBytes(20));

    /// <summary>
    /// Vérifie un code à 6 chiffres pour un instant donné, en tolérant une fenêtre
    /// avant / après (dérive d'horloge). Comparaison à temps constant.
    /// </summary>
    public static bool Verify(string base32Secret, string code, DateTimeOffset now, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim().Replace(" ", "");
        if (code.Length != Digits) return false;

        byte[] key;
        try { key = FromBase32(base32Secret); }
        catch { return false; }

        var counter = now.ToUnixTimeSeconds() / PeriodSeconds;
        for (var offset = -window; offset <= window; offset++)
        {
            var candidate = Compute(key, counter + offset);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(code)))
                return true;
        }
        return false;
    }

    /// <summary>Code TOTP à 6 chiffres pour un instant donné (symétrique de <see cref="Verify"/>).</summary>
    public static string Generate(string base32Secret, DateTimeOffset now)
        => Compute(FromBase32(base32Secret), now.ToUnixTimeSeconds() / PeriodSeconds);

    /// <summary>URI <c>otpauth://</c> à encoder en QR code pour l'enrôlement.</summary>
    public static string BuildUri(string base32Secret, string issuer, string account)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        var iss = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={iss}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    private static string Compute(byte[] key, long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        var hash = HMACSHA1.HashData(key, counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   | (hash[offset + 3] & 0xFF);
        return (binary % (int)Math.Pow(10, Digits)).ToString().PadLeft(Digits, '0');
    }

    private static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0) sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return sb.ToString();
    }

    private static byte[] FromBase32(string value)
    {
        value = value.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var bytes = new List<byte>(value.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in value)
        {
            var idx = Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException("Caractère Base32 invalide.");
            buffer = (buffer << 5) | idx;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return [.. bytes];
    }
}
