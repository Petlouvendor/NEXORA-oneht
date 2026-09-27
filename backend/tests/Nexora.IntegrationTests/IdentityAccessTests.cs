using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Contracts.Identity;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>Contrôle d'accès : rôles, permissions, affectations temporaires, périmètre (Phase 22 §9, §18, §32, §45).</summary>
[Collection("finance")]
public sealed class IdentityAccessTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");   // Pharmacie ABC
    private static readonly Guid CompanyResto = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Cashier = Guid.Parse("dce00000-0000-0000-0000-0000000000d3");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<Guid> NewUser(string label)
    {
        var id = Guid.NewGuid();
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{id}', 'ID-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), '{label}', 'active')
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static (IUserAssignmentService Assign, IPermissionService Perm, IScopeQueryService Scopes, TestTenantContext Tc)
        Services(IServiceProvider sp)
        => (sp.GetRequiredService<IUserAssignmentService>(), sp.GetRequiredService<IPermissionService>(),
            sp.GetRequiredService<IScopeQueryService>(), (TestTenantContext)sp.GetRequiredService<ITenantContext>());

    // ---- §9, §18, §32 : une affectation temporaire expire d'elle-même ----
    [Fact]
    public async Task Temporary_role_assignment_expires_on_its_own()
    {
        await using var s = Scope();
        var (assign, perm, _, tc) = Services(s.ServiceProvider);
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        var target = await NewUser("Auditeur temporaire");

        // Affectation déjà expirée → aucun accès accordé.
        var expired = await assign.GrantAsync(target, new GrantAssignmentRequest(
            "auditor", "COMPANY", Company, null, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1), "Mission passée"));
        expired.IsSuccess.Should().BeTrue(expired.Error?.Message);

        (await perm.LoadProfileAsync(target)).Memberships.Should().BeEmpty();

        // Nouvelle affectation valide jusqu'à demain → l'entreprise devient accessible.
        var active = await assign.GrantAsync(target, new GrantAssignmentRequest(
            "auditor", "COMPANY", Company, null, null, DateTimeOffset.UtcNow.AddDays(1), "Audit trimestriel"));
        active.IsSuccess.Should().BeTrue(active.Error?.Message);

        var profile = await perm.LoadProfileAsync(target);
        profile.Memberships.Should().ContainSingle().Which.CompanyId.Should().Be(Company);
        profile.PermissionsFor(Company).Should().Contain("security.audit_view");
    }

    // ---- §32-33, §44 : la révocation retire immédiatement l'accès (motif obligatoire) ----
    [Fact]
    public async Task Revoked_assignment_is_excluded_from_the_profile()
    {
        await using var s = Scope();
        var (assign, perm, _, tc) = Services(s.ServiceProvider);
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        var target = await NewUser("Comptable détaché");

        var granted = await assign.GrantAsync(target, new GrantAssignmentRequest(
            "accountant", "COMPANY", Company, null, null, null, null));
        (await perm.LoadProfileAsync(target)).Memberships.Should().ContainSingle();

        (await assign.RevokeAsync(granted.Value, new RevokeAssignmentRequest(""))).IsSuccess.Should().BeFalse();
        (await assign.RevokeAsync(granted.Value, new RevokeAssignmentRequest("Fin de mission"))).IsSuccess.Should().BeTrue();

        (await perm.LoadProfileAsync(target)).Memberships.Should().BeEmpty();
        var list = (await assign.ListAssignmentsAsync(target)).Value!;
        list.Should().ContainSingle().Which.Active.Should().BeFalse();
    }

    // ---- §12, §18 : on ne peut accorder que des permissions que l'on détient ----
    [Fact]
    public async Task Cannot_grant_a_role_with_permissions_the_caller_lacks()
    {
        await using var s = Scope();
        var (assign, _, _, tc) = Services(s.ServiceProvider);

        // Un porteur du rôle « cashier » tente d'accorder « chief_accountant ».
        tc.SetUser(Cashier);
        tc.SetScope([Company], [], Company);
        var target = await NewUser("Cible");
        await assign.GrantAsync(target, new GrantAssignmentRequest("cashier", "COMPANY", Company, null, null, null, null));

        var escalation = await assign.GrantAsync(target, new GrantAssignmentRequest(
            "chief_accountant", "COMPANY", Company, null, null, null, null));
        escalation.IsSuccess.Should().BeFalse();
        escalation.Error!.Code.Should().Be("identity.escalation");

        // Le super_administrateur ne s'attribue pas par ce canal.
        tc.SetUser(Direction);
        (await assign.GrantAsync(target, new GrantAssignmentRequest("super_administrator", "COMPANY", Company, null, null, null, null)))
            .Error!.Code.Should().Be("identity.role_protected");
    }

    // ---- §14-16, §39 : le périmètre effectif alimente le sélecteur ----
    [Fact]
    public async Task My_scopes_and_permissions_reflect_effective_access()
    {
        await using var s = Scope();
        var (_, _, scopes, tc) = Services(s.ServiceProvider);

        tc.SetUser(Cashier);
        tc.SetScope([Company], [], Company);
        var tree = await scopes.MyScopesAsync();
        var pharma = tree.Companies.Should().ContainSingle(c => c.Id == Company).Which;
        pharma.Roles.Should().Contain("cashier");
        pharma.ReadOnly.Should().BeFalse();   // le caissier vend

        (await scopes.MyPermissionsAsync()).Should().Contain("pos.sell");

        // La liste des utilisateurs de l'administration est bornée au périmètre.
        tc.SetUser(Direction);
        var users = await s.ServiceProvider.GetRequiredService<IUserAssignmentService>().ListUsersAsync();
        users.Should().Contain(u => u.Email == "direction@abc-group.demo");
        users.Should().OnlyContain(u => !string.IsNullOrEmpty(u.Email));

        // La direction voit les entreprises du groupe.
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        (await scopes.MyScopesAsync()).Companies.Select(c => c.Id).Should().Contain([Company, CompanyResto]);
    }

    // ---- §45 : un company_id forcé ne contourne pas le périmètre ----
    [Fact]
    public async Task Forcing_a_foreign_company_id_grants_no_permission()
    {
        await using var s = Scope();
        var (_, perm, _, tc) = Services(s.ServiceProvider);
        tc.SetUser(Cashier);

        var profile = await perm.LoadProfileAsync(Cashier);
        profile.Memberships.Select(m => m.CompanyId).Should().NotContain(CompanyResto);
        profile.PermissionsFor(CompanyResto).Should().BeEmpty();   // même en « choisissant » c2
        profile.HasPermission("pos.sell", CompanyResto).Should().BeFalse();
    }

    // ---- §6, §34 : suspendre un utilisateur invalide ses sessions (security_epoch) ----
    [Fact]
    public async Task Suspending_a_user_bumps_the_security_epoch()
    {
        await using var s = Scope();
        var (assign, _, _, tc) = Services(s.ServiceProvider);
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        var target = await NewUser("À suspendre");
        await assign.GrantAsync(target, new GrantAssignmentRequest("cashier", "COMPANY", Company, null, null, null, null));

        var before = await db.Database.SqlQuery<DateTimeOffset>($"""
            SELECT security_epoch AS "Value" FROM nexora.app_user WHERE id = {target}
            """).FirstAsync();
        await Task.Delay(15);

        (await assign.SetStatusAsync(target, new SetUserStatusRequest("suspended"))).IsSuccess.Should().BeTrue();

        var status = await db.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM nexora.app_user WHERE id = {target}
            """).FirstAsync();
        var after = await db.Database.SqlQuery<DateTimeOffset>($"""
            SELECT security_epoch AS "Value" FROM nexora.app_user WHERE id = {target}
            """).FirstAsync();
        status.Should().Be("suspended");
        after.Should().BeAfter(before);
    }
}
