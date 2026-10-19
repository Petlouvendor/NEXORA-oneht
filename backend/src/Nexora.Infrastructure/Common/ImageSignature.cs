namespace Nexora.Infrastructure.Common;

/// <summary>
/// Format d'image reconnu par la signature du contenu (audit 2026-10 éd. 2) : le type déclaré par l'appelant était
/// stocké et renvoyé tel quel — un « image/png » pouvait contenir du HTML, ou une pièce jointe déclarée « text/html »
/// être servie en ligne. Seuls PNG, JPEG et WebP sont reconnus ; le type enregistré est celui du contenu.
/// </summary>
public static class ImageSignature
{
    public static string? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
            return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "image/jpeg";
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
            return "image/webp";
        return null;
    }
}
