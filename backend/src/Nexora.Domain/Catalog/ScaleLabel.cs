namespace Nexora.Domain.Catalog;

/// <summary>
/// Étiquette de balance à poids intégré (audit 2026-10, A-015) — convention GS1 des codes à usage interne :
/// EAN-13 commençant par 2, <c>2 F AAAAA PPPPP C</c> — les 7 premiers chiffres désignent l'article (c'est le code
/// enregistré sur le code-barres de l'article, marqué <c>is_weight_embedded</c>), les 5 suivants le poids en grammes,
/// le dernier la clé de contrôle. Le même décodage existe côté caisse hors ligne (<c>scaleLabel.ts</c>).
/// </summary>
public static class ScaleLabel
{
    public const int PrefixLength = 7;

    public static bool TryDecode(string? code, out string prefix, out decimal kilograms)
    {
        prefix = "";
        kilograms = 0m;
        code = code?.Trim() ?? "";
        if (code.Length != 13 || code[0] != '2' || !code.All(char.IsAsciiDigit)) return false;

        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (code[i] - '0') * (i % 2 == 0 ? 1 : 3);
        if ((10 - sum % 10) % 10 != code[12] - '0') return false;

        var grams = int.Parse(code.AsSpan(PrefixLength, 5));
        if (grams == 0) return false;
        prefix = code[..PrefixLength];
        kilograms = grams / 1000m;
        return true;
    }
}
