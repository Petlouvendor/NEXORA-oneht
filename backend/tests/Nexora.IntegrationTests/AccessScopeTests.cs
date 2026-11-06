using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Intelligence;
using Nexora.Contracts.Identity;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 éd. 2, lot 5 — périmètre des droits :
/// A-048, le centre d'intelligence servait aux responsables stock et achats les créances nominatives et la trésorerie ;
/// A-033, la gestion des utilisateurs, vérifiée sur l'entreprise active, s'étendait à toute entreprise d'appartenance.
/// </summary>
[Collection("finance")]
public sealed class AccessScopeTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private AsyncServiceScope Scope(Guid user, Guid company)
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([company], [], company);
        return s;
    }

    private async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> UserAsync(params (string Role, Guid Company)[] roles)
    {
        var id = Guid.NewGuid();
        await ExecAsync($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{id}', 'scope-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), 'Périmètre {id:N}', 'active');
            """);
        foreach (var (role, company) in roles)
            await ExecAsync($"""
                INSERT INTO nexora.user_role (user_id, role_id, scope_id)
                SELECT '{id}', r.id, sc.id FROM nexora.role r, nexora.access_scope sc
                WHERE r.name = '{role}' AND r.system_role AND sc.scope_type = 'COMPANY' AND sc.company_id = '{company}'
                LIMIT 1;
                """);
        return id;
    }

    [Fact]
    public async Task A_stock_manager_sees_stock_recommendations_but_not_receivables()
    {
        var marker = Guid.NewGuid().ToString("N");
        await ExecAsync($"""
            INSERT INTO nexora.recommendation (company_id, category, rule_key, title, why, proposed_action, period_start, drill_path)
            VALUES ('{Company}', 'stock_reorder', 'test', 'Réapprovisionner {marker}', 'stock bas', 'commander', current_date, '/stock/{marker}'),
                   ('{Company}', 'receivable_priority', 'test', 'Relancer le client {marker}', 'doit 33 020 HTG', 'relancer', current_date, '/credit/{marker}');
            """);
        var stock = await UserAsync(("stock_manager", Company));

        await using (var s = Scope(stock, Company))
        {
            var visible = await s.ServiceProvider.GetRequiredService<IRecommendationService>().ListAsync(null, null);
            visible.Should().Contain(r => r.Title == $"Réapprovisionner {marker}");
            visible.Should().NotContain(r => r.Title == $"Relancer le client {marker}",
                "les créances nominatives exigent reporting.view_financial (avant : visibles par intelligence.view)");
            (await s.ServiceProvider.GetRequiredService<IRecommendationService>().DashboardAsync()).TopActions
                .Should().NotContain(r => r.Title.Contains(marker) && r.Category == "receivable_priority");
        }

        await using (var s = Scope(Direction, Company))
            (await s.ServiceProvider.GetRequiredService<IRecommendationService>().ListAsync(null, null))
                .Should().Contain(r => r.Title == $"Relancer le client {marker}", "la direction garde la vue complète");
    }

    [Fact]
    public async Task Managing_users_in_one_company_does_not_extend_to_a_company_where_one_is_only_cashier()
    {
        // Entreprise B du même jeu de données (Supermarché) : l'administrateur de A n'y est que caissier.
        var otherCompany = await FirstOtherCompanyAsync();
        var admin = await UserAsync(("owner_direction", Company), ("cashier", otherCompany));
        var target = await UserAsync(("cashier", otherCompany));

        await using var s = Scope(admin, Company);
        ((TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>()).SetScope([Company, otherCompany], [], Company);
        var users = s.ServiceProvider.GetRequiredService<IUserAssignmentService>();

        (await users.ListUsersAsync()).Should().NotContain(u => u.Id == target,
            "le caissier de B n'est pas dans le périmètre de gestion de l'administrateur de A");
        (await users.ListAssignmentsAsync(target)).IsSuccess.Should().BeFalse();
        (await users.GrantAsync(target, new GrantAssignmentRequest("cashier", "COMPANY", otherCompany, null, null, null, null)))
            .IsSuccess.Should().BeFalse("affecter dans B exige admin.manage_users dans B");
    }

    private async Task<Guid> FirstOtherCompanyAsync()
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            SELECT co.id FROM nexora.company co
            WHERE co.id <> '{Company}' AND co.status = 'active'
              AND EXISTS (SELECT 1 FROM nexora.access_scope sc WHERE sc.scope_type = 'COMPANY' AND sc.company_id = co.id)
            ORDER BY co.created_at LIMIT 1
            """, c);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }
}
