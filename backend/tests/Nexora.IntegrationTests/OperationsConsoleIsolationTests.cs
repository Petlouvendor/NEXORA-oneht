using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Contracts.Operations;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10, A-031 : la console d'exploitation agit sur toute l'installation, mais ses permissions sont portées
/// par la direction d'une entreprise. Dès qu'un autre client est hébergé, un directeur ne doit plus geler les écritures
/// de la plateforme, piloter les tâches planifiées, ni lire ou corriger les données d'un autre client.
/// </summary>
[Collection("finance")]
public sealed class OperationsConsoleIsolationTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private AsyncServiceScope Scope(Guid? user = null)
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(user ?? Direction);
        tc.SetScope([Company], [], Company);
        return s;
    }

    private async Task Sql(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task With_another_client_hosted_a_company_director_stays_within_his_company()
    {
        // Un autre client : entreprise autonome, hors du groupe ABC, avec un client et une demande de correction.
        var other = Guid.NewGuid();
        var otherCustomer = Guid.NewGuid();
        var otherRequest = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.company (id, name, default_currency_id)
            VALUES ('{other}', 'Autre client {other:N}', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id)
            VALUES ('{otherCustomer}', '{other}', 'CLI-{otherCustomer:N}', 'Client confidentiel', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            INSERT INTO nexora.data_correction_request (id, table_name, record_id, field_name, old_value, new_value, reason, requested_by, company_id)
            VALUES ('{otherRequest}', 'customer', '{otherCustomer}', 'phone', '+509 0000', '+509 1111', 'Autre client', '{Direction}', '{other}');
            """);
        try
        {
            await using var s = Scope();
            var sp = s.ServiceProvider;

            // Opérations de portée plateforme : refusées.
            (await sp.GetRequiredService<IMaintenanceService>().ScheduleAsync(new ScheduleMaintenanceRequest(
                    DateTimeOffset.UtcNow.AddDays(1), 30, "Test", ["api"], "Direction", "Aucun", null)))
                .Error!.Code.Should().Be("operations.platform_only");
            (await sp.GetRequiredService<IJobRegistry>().PauseAsync("maintenance.purge"))
                .Error!.Code.Should().Be("operations.platform_only");
            (await sp.GetRequiredService<IRetentionPurgeService>().ExecuteAsync(new PurgePreviewRequest("expired_sessions", null, null)))
                .Error!.Code.Should().Be("operations.platform_only");
            (await sp.GetRequiredService<IFeatureFlagService>().SaveAsync(new SaveFeatureFlagRequest(null, "audit.global", "global", null, null, false)))
                .Error!.Code.Should().Be("operations.platform_only");
            (await sp.GetRequiredService<IOperationsService>().RecordDeploymentAsync(new RecordDeploymentRequest("9.9.9", "production", null)))
                .Error!.Code.Should().Be("operations.platform_only");

            // Bascule de sa propre entreprise : permise.
            (await sp.GetRequiredService<IFeatureFlagService>().SaveAsync(new SaveFeatureFlagRequest(null, "audit.own", "company", Company, null, true)))
                .IsSuccess.Should().BeTrue();

            // Corrections de données : ni lecture, ni demande, ni décision sur l'autre client.
            var corrections = sp.GetRequiredService<IDataCorrectionService>();
            (await corrections.ListAsync(null)).Should().NotContain(r => r.Id == otherRequest);
            (await corrections.RequestAsync(new RequestDataCorrectionRequest("customer", otherCustomer, "phone", "+509 2222", "Essai")))
                .Error!.Code.Should().Be("correction.record_not_found");
            (await corrections.DecideAsync(otherRequest, new DecideDataCorrectionRequest(true, null)))
                .Error!.Code.Should().Be("correction.not_found");
        }
        finally
        {
            // L'autre client est archivé : les autres tests retrouvent une installation d'un seul client.
            await Sql($"UPDATE nexora.company SET status = 'archived' WHERE id = '{other}'");
        }
    }
}
