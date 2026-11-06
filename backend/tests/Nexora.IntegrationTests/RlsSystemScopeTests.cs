using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Audit 2026-10, A-009 : le contexte RLS n'était posé qu'à l'ouverture de la connexion. Dans une transaction, une
/// portée système restait sans effet (exemple constaté : la réinitialisation MFA d'un utilisateur par un administrateur
/// ne supprimait pas ses codes de secours, sans erreur), et une transaction ouverte dans une portée système en gardait
/// la levée de RLS. Tests avec le rôle applicatif réel <c>nexora_app</c> : la fixture par défaut, superutilisateur,
/// contourne toute RLS.
/// </summary>
[Collection("finance")]
public sealed class RlsSystemScopeTests(FinanceEngineFixture fx)
{
    private static readonly Guid Pharmacie = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Restaurant = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid PharmacyScope = Guid.Parse("dce00000-0000-0000-0000-0000000000e2");

    private async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> Count(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static void As(IServiceProvider sp, Guid user, Guid company)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([company], [], company);
    }

    [Fact]
    public async Task Admin_mfa_reset_removes_the_target_recovery_codes()
    {
        var target = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status, mfa_enabled, mfa_secret)
            VALUES ('{target}', 'mfa-{target:N}@test.demo', crypt('x', gen_salt('bf', 4)), 'Cible MFA', 'active', true, 'secret');
            INSERT INTO nexora.user_role (user_id, role_id, scope_id)
            SELECT '{target}', id, '{PharmacyScope}' FROM nexora.role WHERE name = 'cashier' AND system_role LIMIT 1;
            INSERT INTO nexora.mfa_recovery_code (user_id, code_hash) VALUES ('{target}', 'h1'), ('{target}', 'h2');
            """);

        await using var appRole = fx.CreateAppRoleProvider();
        await using (var s = appRole.CreateAsyncScope())
        {
            As(s.ServiceProvider, Direction, Pharmacie);
            var r = await s.ServiceProvider.GetRequiredService<IMfaService>().ResetForUserAsync(target);
            r.IsSuccess.Should().BeTrue(r.Error?.Message);
        }

        (await Count($"SELECT COUNT(*) FROM nexora.mfa_recovery_code WHERE user_id = '{target}'"))
            .Should().Be(0, "les codes de secours de la cible doivent disparaître avec sa MFA");
        (await Count($"SELECT COUNT(*) FROM nexora.app_user WHERE id = '{target}' AND mfa_enabled"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Inside_a_transaction_the_system_scope_applies_and_ends_with_it()
    {
        var foreign = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.customer (id, company_id, customer_number, name, default_currency_id)
            VALUES ('{foreign}', '{Restaurant}', 'CLI-{foreign:N}', 'Client du restaurant', (SELECT id FROM nexora.currency WHERE code = 'HTG'));
            """);

        await using var appRole = fx.CreateAppRoleProvider();
        await using var s = appRole.CreateAsyncScope();
        As(s.ServiceProvider, Direction, Pharmacie);
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var tenant = s.ServiceProvider.GetRequiredService<ITenantContext>();
        Task<int> Visible(CancellationToken t) => db.Database.SqlQuery<int>(
            $"SELECT COUNT(*)::int AS \"Value\" FROM nexora.customer WHERE id = {foreign}").FirstAsync(t);

        var (before, during, after) = await s.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(async t =>
        {
            var b = await Visible(t);
            int d;
            using (tenant.BeginSystemScope()) d = await Visible(t);
            var a = await Visible(t);
            return (b, d, a);
        });

        before.Should().Be(0, "client d'une autre entreprise : invisible");
        during.Should().Be(1, "la portée système ouverte dans la transaction lève la RLS");
        after.Should().Be(0, "la RLS revient dès la fin de la portée, même transaction");
    }

    /// <summary>
    /// Audit 2026-10, A-029 et A-030 (V101) : la base refuse elle-même de modifier un rôle système partagé par toutes
    /// les entreprises, d'écrire dans le journal d'audit d'une autre entreprise, et au rôle applicatif d'écrire dans le
    /// registre des migrations.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_writes_on_shared_roles_foreign_audit_and_the_migration_registry()
    {
        await using var appRole = fx.CreateAppRoleProvider();
        await using var s = appRole.CreateAsyncScope();
        As(s.ServiceProvider, Direction, Pharmacie);
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();

        (await db.Database.ExecuteSqlAsync($"UPDATE nexora.role SET description = 'piraté' WHERE system_role AND name = 'cashier'"))
            .Should().Be(0, "un rôle système n'est pas modifiable par une session d'entreprise");
        (await db.Database.ExecuteSqlAsync($"""
            DELETE FROM nexora.role_permission WHERE role_id = (SELECT id FROM nexora.role WHERE system_role AND name = 'cashier')
            """)).Should().Be(0, "ni ses permissions");

        var foreignAudit = async () => await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.audit_log (company_id, action, entity_type) VALUES ({Restaurant}, 'INSERT', 'test')
            """);
        await foreignAudit.Should().ThrowAsync<Exception>("on n'écrit pas dans le journal d'une autre entreprise");

        var registry = async () => await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.schema_migration (version, filename, checksum) VALUES ('V998', 'V998__x.sql', 'x')
            """);
        await registry.Should().ThrowAsync<Exception>("le registre des migrations n'est écrit que par le compte propriétaire");
    }
}
