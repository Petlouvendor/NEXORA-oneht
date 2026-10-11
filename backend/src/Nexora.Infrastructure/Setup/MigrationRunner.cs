using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Nexora.Infrastructure.Setup;

/// <summary>Écart entre les migrations embarquées dans l'application et celles enregistrées dans la base.</summary>
/// <param name="Pending">Migrations embarquées pas encore appliquées : l'application attend un schéma absent.</param>
/// <param name="Drifted">Migrations appliquées dont le fichier a changé depuis (une migration appliquée ne se modifie jamais).</param>
/// <param name="Unknown">Migrations appliquées absentes de l'application : la base est plus récente que le code déployé
/// (retour arrière de l'application après une migration).</param>
public sealed record MigrationStatus(
    int Embedded, int Applied, string? LatestApplied, string? LatestEmbedded,
    IReadOnlyList<string> Pending, IReadOnlyList<string> Drifted, IReadOnlyList<string> Unknown)
{
    public bool UpToDate => Pending.Count == 0;
    public bool Consistent => Drifted.Count == 0 && Unknown.Count == 0;
}

/// <summary>
/// Moteur de migrations de production (finalisation phase 18, §39 « migrations ») : mêmes fichiers et même suivi
/// <c>nexora.schema_migration</c> que <c>database/scripts/apply.sh</c> et l'assistant d'installation, mais utilisable
/// sans <c>psql</c> ni dépôt — depuis l'image de l'API (<c>dotnet Nexora.Api.dll migrate</c>).
/// <list type="bullet">
/// <item>chaque migration dans sa transaction, enregistrée avec son empreinte SHA-256 ;</item>
/// <item>verrou consultatif PostgreSQL : deux déploiements simultanés ne migrent jamais en même temps ;</item>
/// <item>refus d'appliquer quoi que ce soit si une migration déjà appliquée a été modifiée (empreinte différente,
/// fins de ligne Windows/Linux tolérées) ;</item>
/// <item>état lisible avec le rôle applicatif (sonde readiness, checklist de mise en production).</item>
/// </list>
/// </summary>
public static class MigrationRunner
{
    /// <summary>Clé du verrou consultatif (« NXMIGR » en ASCII).</summary>
    public const long AdvisoryLockKey = 0x4E584D494752;

    public static async Task<MigrationStatus> StatusAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        var embedded = EmbeddedMigrations.All();
        var applied = await AppliedAsync(conn, ct);
        var byVersion = embedded.ToDictionary(m => m.Version, StringComparer.Ordinal);

        var pending = embedded.Where(m => !applied.ContainsKey(m.Version)).Select(m => m.FileName).ToList();
        var drifted = embedded
            .Where(m => applied.TryGetValue(m.Version, out var stored) && stored is not null && !m.Matches(stored))
            .Select(m => m.FileName).ToList();
        var unknown = applied.Keys.Where(v => !byVersion.ContainsKey(v)).OrderBy(v => v, StringComparer.Ordinal).ToList();

        return new MigrationStatus(embedded.Count, applied.Count,
            applied.Keys.OrderBy(v => v, StringComparer.Ordinal).LastOrDefault(),
            embedded.LastOrDefault()?.Version, pending, drifted, unknown);
    }

    /// <summary>
    /// Applique les migrations en attente, sous verrou consultatif. Refuse (sans rien appliquer) si une migration
    /// appliquée a été modifiée, sauf <paramref name="allowDrift"/>. Rend le nombre de migrations appliquées.
    /// </summary>
    public static async Task<int> ApplyPendingAsync(NpgsqlConnection conn, CancellationToken ct = default,
        bool allowDrift = false, Action<string>? progress = null)
    {
        var migrations = EmbeddedMigrations.All();
        if (migrations.Count == 0)
            throw new InvalidOperationException("Aucune migration embarquée dans l'application (database/migrations).");

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn))
        {
            lockCmd.Parameters.AddWithValue("k", AdvisoryLockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        try
        {
            // Relu sous verrou : un autre déploiement a pu migrer pendant l'attente.
            var status = await StatusAsync(conn, ct);
            if (status.Drifted.Count > 0 && !allowDrift)
                throw new InvalidOperationException(
                    "Migrations déjà appliquées modifiées depuis (empreinte différente) : " + string.Join(", ", status.Drifted) +
                    ". Une migration appliquée ne se modifie jamais : créez une nouvelle migration.");

            var pending = new HashSet<string>(status.Pending, StringComparer.Ordinal);
            var count = 0;
            foreach (var m in migrations.Where(m => pending.Contains(m.FileName)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                try
                {
                    await using (var cmd = new NpgsqlCommand(m.Sql, conn, tx) { CommandTimeout = 600 })
                        await cmd.ExecuteNonQueryAsync(ct);
                    await using (var log = new NpgsqlCommand("""
                        INSERT INTO nexora.schema_migration (version, filename, checksum) VALUES (@v, @f, @c)
                        ON CONFLICT (version) DO NOTHING
                        """, conn, tx))
                    {
                        log.Parameters.AddWithValue("v", m.Version);
                        log.Parameters.AddWithValue("f", m.FileName);
                        log.Parameters.AddWithValue("c", m.Checksum);
                        await log.ExecuteNonQueryAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                    count++;
                    progress?.Invoke(m.FileName);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Échec de la migration {m.FileName} : {ex.Message}", ex);
                }
            }
            return count;
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            unlock.Parameters.AddWithValue("k", AdvisoryLockKey);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Réaligne l'empreinte d'une migration appliquée dont le fichier a été retouché (ex. corrigé avant son commit
    /// après avoir été joué en local) : la version actuelle du fichier est REJOUÉE dans une transaction — elle doit donc
    /// être idempotente et passer sur le schéma existant — et seulement alors son empreinte est enregistrée. Rien n'est
    /// réécrit si le rejeu échoue.
    /// </summary>
    public static async Task RealignAsync(NpgsqlConnection conn, string version, CancellationToken ct = default)
    {
        var m = EmbeddedMigrations.All().FirstOrDefault(x => x.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Migration {version} inconnue de cette version.");
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var check = new NpgsqlCommand("SELECT 1 FROM nexora.schema_migration WHERE version = @v FOR UPDATE", conn, tx))
        {
            check.Parameters.AddWithValue("v", m.Version);
            if (await check.ExecuteScalarAsync(ct) is null)
                throw new InvalidOperationException($"{m.FileName} n'est pas appliquée : rien à réaligner (lancer « migrate »).");
        }
        await using (var replay = new NpgsqlCommand(m.Sql, conn, tx) { CommandTimeout = 600 })
            await replay.ExecuteNonQueryAsync(ct);
        await using (var update = new NpgsqlCommand(
            "UPDATE nexora.schema_migration SET checksum = @c, filename = @f WHERE version = @v", conn, tx))
        {
            update.Parameters.AddWithValue("c", m.Checksum);
            update.Parameters.AddWithValue("f", m.FileName);
            update.Parameters.AddWithValue("v", m.Version);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task<Dictionary<string, string?>> AppliedAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var applied = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('nexora.schema_migration') IS NOT NULL", conn))
            if (await exists.ExecuteScalarAsync(ct) is not true) return applied;

        await using var cmd = new NpgsqlCommand("SELECT version, checksum FROM nexora.schema_migration", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            applied[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return applied;
    }
}

/// <summary>Migrations SQL embarquées (<c>database/migrations/V*.sql</c>), dans l'ordre des fichiers.</summary>
internal static class EmbeddedMigrations
{
    /// <param name="Checksum">Empreinte du fichier tel qu'embarqué (même calcul que <c>sha256sum</c> dans apply.sh).</param>
    /// <param name="Accepted">Empreintes équivalentes : le même fichier en fins de ligne LF ou CRLF (poste Windows avec
    /// <c>core.autocrlf</c>, image construite sous Linux).</param>
    internal sealed record Migration(string Version, string FileName, string Sql, string Checksum, IReadOnlySet<string> Accepted)
    {
        public bool Matches(string stored) => Accepted.Contains(stored.Trim().ToLowerInvariant());
    }

    private const string Prefix = "migrations/";
    private static IReadOnlyList<Migration>? _cache;

    public static IReadOnlyList<Migration> All() => _cache ??= Load();

    private static List<Migration> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                var file = n[Prefix.Length..];
                var sql = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
                var checksum = Sha256(bytes);
                var lf = ReplaceBytes(bytes, "\r\n"u8.ToArray(), "\n"u8.ToArray());
                var crlf = ReplaceBytes(lf, "\n"u8.ToArray(), "\r\n"u8.ToArray());
                return new Migration(file[..file.IndexOf("__", StringComparison.Ordinal)], file, sql, checksum,
                    new HashSet<string>([checksum, Sha256(lf), Sha256(crlf)], StringComparer.Ordinal));
            })
            .OrderBy(m => m.FileName, StringComparer.Ordinal)
            .ToList();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] ReplaceBytes(byte[] source, byte[] from, byte[] to)
    {
        var output = new List<byte>(source.Length + source.Length / 20);
        for (var i = 0; i < source.Length; i++)
        {
            if (i + from.Length <= source.Length && source.AsSpan(i, from.Length).SequenceEqual(from))
            {
                output.AddRange(to);
                i += from.Length - 1;
            }
            else output.Add(source[i]);
        }
        return output.ToArray();
    }
}
