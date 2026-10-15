using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Security;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Security;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10 : A-010 — la garde anti-élévation ne protégeait que l'attribution d'un rôle : un administrateur des
/// utilisateurs d'une société pouvait suspendre la direction ou lui retirer son rôle. A-032 — la révocation d'une
/// session d'autrui révoquait quand même ses jetons de rafraîchissement.
/// </summary>
[Collection("finance")]
public sealed class AccountAuthorityTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid PharmacyScope = Guid.Parse("dce00000-0000-0000-0000-0000000000e2");

    private AsyncServiceScope Scope(Guid user)
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([Company], [], Company);
        return s;
    }

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    /// <summary>Administrateur des utilisateurs de la pharmacie, avec un rôle personnalisé limité.</summary>
    private async Task<Guid> NewUserAdmin()
    {
        var user = Guid.NewGuid();
        var role = Guid.NewGuid();
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{user}', 'uadm-{user:N}@test.demo', crypt('x', gen_salt('bf', 4)), 'Admin utilisateurs', 'active');
            INSERT INTO nexora.role (id, company_id, name, system_role, status) VALUES ('{role}', '{Company}', 'Admin utilisateurs {role:N}', false, 'active');
            INSERT INTO nexora.role_permission (role_id, permission_id)
            SELECT '{role}', id FROM nexora.permission WHERE code IN ('admin.manage_users', 'pos.view', 'pos.sell');
            INSERT INTO nexora.user_role (user_id, role_id, scope_id) VALUES ('{user}', '{role}', '{PharmacyScope}');
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return user;
    }

    [Fact]
    public async Task A_user_administrator_cannot_suspend_or_strip_a_more_privileged_account()
    {
        var admin = await NewUserAdmin();
        var director = await fx.NewCompanyDirectorAsync(Company);
        var directorAssignment = await Scalar<Guid>($"SELECT id FROM nexora.user_role WHERE user_id = '{director}'");

        await using (var s = Scope(admin))
        {
            var svc = s.ServiceProvider.GetRequiredService<IUserAssignmentService>();
            (await svc.SetStatusAsync(director, new SetUserStatusRequest("suspended")))
                .Error!.Code.Should().Be("identity.escalation");
            (await svc.RevokeAsync(directorAssignment, new RevokeAssignmentRequest("Essai")))
                .Error!.Code.Should().Be("identity.escalation");
        }
        (await Scalar<string>($"SELECT status FROM nexora.app_user WHERE id = '{director}'")).Should().Be("active");
        (await Scalar<DateTimeOffset?>($"SELECT revoked_at FROM nexora.user_role WHERE id = '{directorAssignment}'")).Should().BeNull();

        // La direction, elle, peut suspendre l'administrateur moins habilité.
        await using (var s = Scope(Direction))
            (await s.ServiceProvider.GetRequiredService<IUserAssignmentService>()
                .SetStatusAsync(admin, new SetUserStatusRequest("suspended"))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Revoking_someone_else_session_leaves_its_refresh_tokens_alone()
    {
        var victim = await fx.NewCompanyDirectorAsync(Company);
        var attacker = await NewUserAdmin();
        var session = Guid.NewGuid();
        await using (var s = Scope(victim))
            await s.ServiceProvider.GetRequiredService<ISessionSecurityService>()
                .StartAsync(session, victim, Company, "127.0.0.1", "test", null);

        await using (var s = Scope(attacker))
            (await s.ServiceProvider.GetRequiredService<ISessionSecurityService>()
                    .RevokeAsync(session, new RevokeSessionRequest("Essai")))
                .Error!.Code.Should().Be("security.session");

        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.auth_refresh_token WHERE session_id = '{session}' AND revoked_at IS NULL"))
            .Should().BeGreaterThan(0, "les jetons de la victime restent valides");
    }
}
