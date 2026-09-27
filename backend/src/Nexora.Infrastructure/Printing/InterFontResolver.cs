using PdfSharp.Fonts;

namespace Nexora.Infrastructure.Printing;

/// <summary>
/// Polices du PDF : Inter (police du design system), embarquée dans l'assembly. Toute famille demandée
/// est servie par Inter — le rendu est identique sous Windows et dans l'image Linux sans police.
/// </summary>
internal sealed class InterFontResolver : IFontResolver
{
    public const string Family = "Inter";

    private static readonly object Gate = new();
    private static bool _installed;

    /// <summary>Installe le résolveur une seule fois par processus (PDFsharp l'interdit ensuite).</summary>
    public static void EnsureInstalled()
    {
        if (_installed) return;
        lock (Gate)
        {
            if (_installed) return;
            if (GlobalFontSettings.FontResolver is not InterFontResolver)
                GlobalFontSettings.FontResolver = new InterFontResolver();
            _installed = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? "Inter-Bold" : isItalic ? "Inter-Italic" : "Inter-Regular");

    public byte[]? GetFont(string faceName)
    {
        using var stream = typeof(InterFontResolver).Assembly.GetManifestResourceStream($"fonts/{faceName}.ttf")
            ?? throw new InvalidOperationException($"Police embarquée « {faceName} » introuvable.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
