using FluentAssertions;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10, priorité 8 : l'unité de travail renvoyait au client le texte brut de PostgreSQL (« duplicate key
/// value violates unique constraint "…" ») ; les refus métier des déclencheurs doivent, eux, rester lisibles.
/// </summary>
[Collection("finance")]
public sealed class DatabaseErrorsTests(FinanceEngineFixture fx)
{
    private async Task<PostgresException> Fail(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        var act = () => cmd.ExecuteNonQueryAsync();
        return (await act.Should().ThrowAsync<PostgresException>()).Which;
    }

    [Fact]
    public async Task A_constraint_violation_reaches_the_client_without_table_or_constraint_names()
    {
        var pg = await Fail("INSERT INTO nexora.company SELECT * FROM nexora.company LIMIT 1");

        pg.ConstraintName.Should().NotBeNullOrEmpty();
        var message = DatabaseErrors.ClientMessage(pg);
        message.Should().Be("Cet enregistrement existe déjà.");
        message.Should().NotContain(pg.ConstraintName!).And.NotContain("company");
    }

    [Fact]
    public async Task A_business_refusal_raised_by_a_trigger_keeps_its_message()
    {
        var pg = await Fail("DELETE FROM nexora.journal_entry WHERE id = (SELECT id FROM nexora.journal_entry WHERE status = 'posted' LIMIT 1)");

        pg.SqlState.Should().Be(PostgresErrorCodes.CheckViolation, "le déclencheur lève check_violation");
        DatabaseErrors.ClientMessage(pg).Should().Be(pg.MessageText).And.Contain("suppression interdite");
    }
}
