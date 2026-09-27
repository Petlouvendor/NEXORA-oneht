using System.Text.RegularExpressions;

namespace Nexora.Infrastructure.Notifications;

/// <summary>
/// Rendu de gabarit sûr (Phase 34 §23) : substitue uniquement les jetons <c>{{clef}}</c> dont la
/// valeur est fournie dans le contexte. Une variable absente devient une chaîne vide et est
/// signalée. Aucune évaluation, aucune interpolation récursive : le contenu injecté n'est jamais
/// ré-interprété comme un gabarit.
/// </summary>
internal static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}")]
    private static partial Regex TokenRegex();

    public static (string Text, IReadOnlyList<string> Missing) Render(
        string template, IReadOnlyDictionary<string, string?> context)
    {
        if (string.IsNullOrEmpty(template)) return ("", []);
        var missing = new List<string>();

        var result = TokenRegex().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (context.TryGetValue(key, out var value) && value is not null)
                return value;
            if (!missing.Contains(key)) missing.Add(key);
            return "";
        });

        return (result, missing);
    }

    /// <summary>Jetons <c>{{…}}</c> présents dans un gabarit — pour la liste blanche déclarative.</summary>
    public static IReadOnlyList<string> Tokens(string template)
    {
        if (string.IsNullOrEmpty(template)) return [];
        var seen = new List<string>();
        foreach (Match m in TokenRegex().Matches(template))
        {
            var key = m.Groups[1].Value;
            if (!seen.Contains(key)) seen.Add(key);
        }
        return seen;
    }
}
