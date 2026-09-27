using FluentAssertions;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Règles de cohérence du modèle de données appliquées par PostgreSQL (Phase 17 §58-59).
/// Tests au niveau base : contraintes CHECK / FK composites / UNIQUE / triggers, sans passer
/// par les services. Connexion superutilisateur (RLS contournée) — seules les contraintes comptent.
/// </summary>
[Collection("finance")]
public sealed class DataModelIntegrityTests(FinanceEngineFixture fx)
{
    private const string Company = "dce00000-0000-0000-0000-0000000000c1";   // Pharmacie ABC
    private const string CompanyResto = "dce00000-0000-0000-0000-0000000000c2";
    private const string Store = "dce00000-0000-0000-0000-00000000a001";
    private const string Location = "dce00000-0000-0000-0000-00000000a003";
    private const string Product = "dce00000-0000-0000-0000-00000000a041";
    private const string CashAccount = "dce00000-0000-0000-0000-00000000a051";

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        return c;
    }

    private static async Task<PostgresException> ExpectFailure(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        var act = async () => await cmd.ExecuteNonQueryAsync();
        return (await act.Should().ThrowAsync<PostgresException>()).Which;
    }

    // ---- §16, §58 : un mouvement de stock doit avoir une cause identifiable ----
    [Fact]
    public async Task Stock_movement_without_a_cause_is_rejected()
    {
        await using var c = await OpenAsync();
        var ex = await ExpectFailure(c, $"""
            INSERT INTO nexora.stock_movement (company_id, product_id, inventory_location_id, movement_type, quantity, unit_cost)
            VALUES ('{Company}', '{Product}', '{Location}', 'adjustment', 5, 10)
            """);
        ex.SqlState.Should().Be("23514");
        ex.ConstraintName.Should().Be("stock_movement_cause_chk");
    }

    // ---- §30, §58 : un paiement doit avoir une source ou un contexte financier ----
    [Fact]
    public async Task Payment_without_any_context_is_rejected()
    {
        await using var c = await OpenAsync();
        var ex = await ExpectFailure(c, $"""
            INSERT INTO nexora.payment (company_id, payment_number, direction, currency_id, amount)
            VALUES ('{Company}', 'PMT-NOCTX-{Guid.NewGuid():N}', 'inbound', (SELECT id FROM nexora.currency WHERE code='HTG'), 100)
            """);
        ex.ConstraintName.Should().Be("payment_context_chk");
    }

    // ---- §8, §58 : une ligne de vente ne peut appartenir à une autre entreprise ----
    [Fact]
    public async Task Sale_line_cannot_cross_company_boundary()
    {
        await using var c = await OpenAsync();
        var saleId = Guid.NewGuid();
        await using (var mk = new NpgsqlCommand($"""
            INSERT INTO nexora.sale (id, company_id, store_id, document_number, status, sale_type, currency_id, subtotal, tax_total, total)
            VALUES ('{saleId}', '{Company}', '{Store}', 'DMI-{saleId:N}', 'draft', 'cash',
                    (SELECT id FROM nexora.currency WHERE code='HTG'), 0, 0, 0)
            """, c))
            await mk.ExecuteNonQueryAsync();

        var ex = await ExpectFailure(c, $"""
            INSERT INTO nexora.sale_line (sale_id, company_id, product_id, quantity, unit_price, line_total, cost_snapshot)
            VALUES ('{saleId}', '{CompanyResto}', '{Product}', 1, 10, 10, 0)
            """);
        ex.SqlState.Should().Be("23503");   // foreign_key_violation sur (sale_id, company_id)
    }

    // ---- §40, §58, §59 : un événement offline possède une clé unique ----
    [Fact]
    public async Task Duplicate_offline_operation_uuid_is_rejected()
    {
        await using var c = await OpenAsync();
        var op = Guid.NewGuid();
        var device = Guid.NewGuid();
        await using (var dev = new NpgsqlCommand($"""
            INSERT INTO nexora.sync_device (id, company_id, device_identifier, device_type, status)
            VALUES ('{device}', '{Company}', 'DMI-{device:N}', 'pos', 'active')
            """, c))
            await dev.ExecuteNonQueryAsync();

        string Insert() => $"""
            INSERT INTO nexora.sync_operation (company_id, device_id, operation_uuid, entity_type, operation_type, payload, status)
            VALUES ('{Company}', '{device}', '{op}', 'sale', 'create', 'null'::jsonb, 'PENDING')
            """;
        await using (var first = new NpgsqlCommand(Insert(), c))
            await first.ExecuteNonQueryAsync();

        var ex = await ExpectFailure(c, Insert());
        ex.SqlState.Should().Be("23505");   // unique_violation
        ex.ConstraintName.Should().Be("sync_operation_uuid_uk");
    }

    // ---- §35, §58 : une écriture appartient à une période et un journal ----
    [Fact]
    public async Task Journal_entry_without_period_or_journal_is_rejected()
    {
        await using var c = await OpenAsync();
        var ex = await ExpectFailure(c, $"""
            INSERT INTO nexora.journal_entry (company_id, entry_number, entry_date, status)
            VALUES ('{Company}', 'JE-{Guid.NewGuid():N}', current_date, 'draft')
            """);
        ex.SqlState.Should().Be("23502");   // not_null_violation (journal_id / accounting_period_id)
    }

    // ---- §52 : un mouvement de stock validé ne se supprime pas physiquement ----
    [Fact]
    public async Task Validated_stock_movement_cannot_be_hard_deleted()
    {
        await using var c = await OpenAsync();
        var mv = Guid.NewGuid();
        await using (var mk = new NpgsqlCommand($"""
            INSERT INTO nexora.stock_movement (id, company_id, product_id, inventory_location_id, movement_type, quantity, unit_cost, reference_type, reference_id)
            VALUES ('{mv}', '{Company}', '{Product}', '{Location}', 'adjustment', 3, 10, 'stock_adjustment', '{Guid.NewGuid()}')
            """, c))
            await mk.ExecuteNonQueryAsync();

        var ex = await ExpectFailure(c, $"DELETE FROM nexora.stock_movement WHERE id = '{mv}'");
        ex.MessageText.Should().Contain("suppression physique interdite");
    }

    // ---- §7 : une entreprise peut être indépendante (sans groupe) ----
    [Fact]
    public async Task Independent_company_needs_no_group()
    {
        await using var c = await OpenAsync();
        var id = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO nexora.company (id, group_id, name, legal_name, default_currency_id)
            VALUES ('{id}', NULL, 'Indépendante DMI', 'Indépendante DMI S.A.', (SELECT id FROM nexora.currency WHERE code='HTG'));
            DELETE FROM nexora.company WHERE id = '{id}';
            """, c);
        var act = async () => await cmd.ExecuteNonQueryAsync();
        await act.Should().NotThrowAsync();
    }
}
