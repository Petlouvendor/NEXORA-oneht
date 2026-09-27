using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Administration;
using Nexora.Contracts.Administration;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Scheduling;

namespace Nexora.IntegrationTests;

/// <summary>Planificateur de traitements asynchrones du monolithe modulaire (Phase 16 §22).</summary>
[Collection("finance")]
public sealed class SchedulerTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    private async Task Exec(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- §22 : un passage du planificateur exécute les traitements dus, par entreprise ----
    [Fact]
    public async Task Scheduler_tick_runs_due_automations_across_active_companies()
    {
        await using var s = Scope();
        var autos = s.ServiceProvider.GetRequiredService<IAutomationService>();
        var runner = s.ServiceProvider.GetRequiredService<SchedulerRunner>();
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        // Une créance en retard + une automatisation de relance dont l'échéance est passée.
        var customer = Guid.NewGuid();
        await Exec($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id)
              VALUES ('{customer}', '{Company}', 'SCH-{customer:N}', 'Client planif', (SELECT id FROM nexora.currency WHERE code='HTG'));
            INSERT INTO nexora.customer_receivable (company_id, customer_id, currency_id, original_amount, issue_date, due_date, status)
              VALUES ('{Company}', '{customer}', (SELECT id FROM nexora.currency WHERE code='HTG'), 7000, current_date - 40, current_date - 25, 'OPEN');
            """);

        var autoId = (await autos.UpsertAsync(new UpsertAutomationRequest(
            $"sch-{Guid.NewGuid():N}"[..14], "Relance planifiée", "dunning",
            J("""{"frequency":"daily","atTime":"08:00"}"""), J("{}")))).Value;
        await Exec($"UPDATE nexora.automation SET next_run_at = now() - interval '1 hour' WHERE id = '{autoId}'");

        var before = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.notification WHERE company_id = {Company} AND type = 'dunning'
            """).FirstAsync();

        var result = await runner.RunOnceAsync();

        result.Companies.Should().BeGreaterThanOrEqualTo(1);
        result.AutomationsRun.Should().BeGreaterThanOrEqualTo(1);
        result.Failures.Should().Be(0);

        var after = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.notification WHERE company_id = {Company} AND type = 'dunning'
            """).FirstAsync();
        after.Should().BeGreaterThan(before);

        // Idempotent : l'automatisation a été replanifiée, un second passage immédiat ne rejoue rien.
        var second = await runner.RunOnceAsync();
        second.AutomationsRun.Should().Be(0);
    }
}
