using FluentAssertions;

namespace Nexora.UnitTests;

/// <summary>
/// Aucun texte en double encodage dans les sources (audit 2026-10 éd. 2) : un fichier UTF-8 relu en Windows-1252 puis
/// réenregistré affichait « Le compte a changÃ© pendant la reprise. » à l'utilisateur.
/// </summary>
public sealed class SourceEncodingTests
{
    private static readonly string[] Mojibake = ["Ã©", "Ã¨", "Ãª", "Ã ", "Ã§", "Ã´", "Ã¹", "Ã‰", "Â«", "Â»", "â€™", "â€”", "â€“"];
    private static readonly string[] Extensions = [".cs", ".ts", ".tsx", ".sql"];

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "Nexora.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Racine du dépôt introuvable.");
    }

    [Fact]
    public void No_source_file_contains_double_encoded_text()
    {
        var root = RepositoryRoot();
        var folders = new[] { "backend/src", "backend/tests", "frontend/nexora-web/src", "database" }
            .Select(f => Path.Combine(root, f)).Where(Directory.Exists);
        var offenders = folders
            .SelectMany(f => Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories))
            .Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains("node_modules")
                        && !p.EndsWith(nameof(SourceEncodingTests) + ".cs"))
            .Where(p => { var text = File.ReadAllText(p); return Mojibake.Any(text.Contains); })
            .Select(p => Path.GetRelativePath(root, p))
            .ToList();

        offenders.Should().BeEmpty("chaque fichier est en UTF-8 sans double encodage");
    }
}
