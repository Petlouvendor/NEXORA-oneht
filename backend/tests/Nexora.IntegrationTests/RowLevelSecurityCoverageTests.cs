using FluentAssertions;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Analyse exhaustive 2026-10, §5.2 : une table qui porte <c>company_id</c> est protégée par la RLS, sauf exception
/// justifiée ci-dessous. Une nouvelle table d'entreprise oubliée fait échouer la suite.
/// </summary>
[Collection("finance")]
public sealed class RowLevelSecurityCoverageTests(FinanceEngineFixture fx)
{
    private static readonly Dictionary<string, string> Exceptions = new()
    {
        // Résolution de l'identité et des périmètres à la connexion, avant tout contexte d'entreprise.
        ["access_scope"] = "identité",
        // Tables de la plateforme (éditeur), lues hors de toute entreprise (V051, V054).
        ["feature_flag"] = "plateforme",
        ["platform_customer_company"] = "plateforme",
        ["support_access_grant"] = "plateforme",
        ["support_ticket"] = "plateforme",
    };

    [Fact]
    public async Task Every_company_table_is_protected_by_row_level_security()
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT t.relname
            FROM pg_class t
            JOIN pg_namespace n ON n.oid = t.relnamespace AND n.nspname = 'nexora'
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attname = 'company_id' AND NOT a.attisdropped
            WHERE t.relkind = 'r' AND NOT (t.relrowsecurity AND t.relforcerowsecurity)
            """, c);
        var unprotected = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) unprotected.Add(r.GetString(0));

        unprotected.Should().BeEquivalentTo(Exceptions.Keys,
            "toute table d'entreprise doit avoir la RLS active et forcée, ou figurer dans les exceptions justifiées");
    }
}
