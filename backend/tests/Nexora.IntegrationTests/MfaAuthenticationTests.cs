using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.UseCases.Auth;
using Nexora.Contracts.Auth;
using Nexora.Infrastructure.Identity;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>Authentification multi-facteur & durcissement de la connexion — Phase 33 §7-9, §48.</summary>
[Collection("finance")]
public sealed class MfaAuthenticationTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Cashier = Guid.Parse("dce00000-0000-0000-0000-0000000000d3");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    private async Task<Guid> NewUser(string password)
    {
        var id = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{id}', 'mfa-{id:N}@test.demo', crypt('{password}', gen_salt('bf', 4)), 'MFA test', 'active')
            """);
        return id;
    }

    private static void AsUser(IServiceProvider sp, Guid userId)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(userId);
        tc.SetScope([Company], [], Company);
    }

    // ---- §8 : enrôlement → confirmation active la MFA et délivre des codes de récupération ----
    [Fact]
    public async Task Enroll_then_confirm_activates_mfa_and_returns_recovery_codes()
    {
        await using var s = Scope();
        var uid = await NewUser("pw-enroll");
        AsUser(s.ServiceProvider, uid);
        var mfa = s.ServiceProvider.GetRequiredService<IMfaService>();

        var begin = await mfa.BeginEnrollmentAsync();
        begin.IsSuccess.Should().BeTrue(begin.Error?.Message);
        begin.Value!.OtpauthUri.Should().StartWith("otpauth://totp/");

        var code = Totp.Generate(begin.Value!.Secret, DateTimeOffset.UtcNow);
        var confirm = await mfa.ConfirmEnrollmentAsync(new MfaConfirmRequest(code));
        confirm.IsSuccess.Should().BeTrue(confirm.Error?.Message);
        confirm.Value!.RecoveryCodes.Should().HaveCount(10);

        var status = await mfa.StatusAsync();
        status.Enabled.Should().BeTrue();
        status.RecoveryCodesRemaining.Should().Be(10);

        (await Scalar<bool>($"SELECT mfa_enabled FROM nexora.app_user WHERE id = '{uid}'")).Should().BeTrue();
        (await Scalar<string>($"SELECT mfa_pending_secret FROM nexora.app_user WHERE id = '{uid}'")).Should().BeNull();
    }

    // ---- §8 : la connexion renvoie un défi, puis un code TOTP valide émet le jeton ----
    [Fact]
    public async Task Login_with_mfa_returns_a_challenge_then_totp_verify_issues_a_token()
    {
        var uid = await NewUser("pw-login");
        string secret;
        await using (var s = Scope())
        {
            AsUser(s.ServiceProvider, uid);
            var mfa = s.ServiceProvider.GetRequiredService<IMfaService>();
            var begin = await mfa.BeginEnrollmentAsync();
            secret = begin.Value!.Secret;
            await mfa.ConfirmEnrollmentAsync(new MfaConfirmRequest(Totp.Generate(secret, DateTimeOffset.UtcNow)));
        }

        await using var s2 = Scope();
        var login = s2.ServiceProvider.GetRequiredService<LoginHandler>();
        var email = await Scalar<string>($"SELECT email FROM nexora.app_user WHERE id = '{uid}'");

        var first = await login.HandleAsync(new LoginRequest(email!, "pw-login", null));
        first.IsSuccess.Should().BeTrue(first.Error?.Message);
        first.Value!.MfaRequired.Should().BeTrue();
        first.Value.AccessToken.Should().BeEmpty();
        first.Value.MfaToken.Should().NotBeNullOrEmpty();

        var verify = s2.ServiceProvider.GetRequiredService<MfaVerifyHandler>();
        // Code du pas suivant : celui de l'enrôlement a déjà servi (anti-rejeu, audit 2026-10, A-011).
        var second = await verify.HandleAsync(
            new MfaVerifyLoginRequest(first.Value.MfaToken!, Totp.Generate(secret, DateTimeOffset.UtcNow.AddSeconds(30))));
        second.IsSuccess.Should().BeTrue(second.Error?.Message);
        second.Value!.AccessToken.Should().NotBeNullOrEmpty();
        second.Value.MfaRequired.Should().BeFalse();

        // Le défi est consommé : impossible de le rejouer (§28 protection replay).
        var replay = await verify.HandleAsync(
            new MfaVerifyLoginRequest(first.Value.MfaToken!, Totp.Generate(secret, DateTimeOffset.UtcNow)));
        replay.IsSuccess.Should().BeFalse();
    }

    // ---- Audit 2026-10, A-011 : un code TOTP accepté ne sert pas une seconde fois, même dans sa fenêtre ----
    [Fact]
    public async Task A_totp_code_already_accepted_is_refused_on_a_new_challenge()
    {
        var uid = await NewUser("pw-replay");
        string secret, enrollCode;
        await using (var s = Scope())
        {
            AsUser(s.ServiceProvider, uid);
            var mfa = s.ServiceProvider.GetRequiredService<IMfaService>();
            secret = (await mfa.BeginEnrollmentAsync()).Value!.Secret;
            enrollCode = Totp.Generate(secret, DateTimeOffset.UtcNow);
            (await mfa.ConfirmEnrollmentAsync(new MfaConfirmRequest(enrollCode))).IsSuccess.Should().BeTrue();
        }

        await using var s2 = Scope();
        var login = s2.ServiceProvider.GetRequiredService<LoginHandler>();
        var verify = s2.ServiceProvider.GetRequiredService<MfaVerifyHandler>();
        var email = await Scalar<string>($"SELECT email FROM nexora.app_user WHERE id = '{uid}'");
        async Task<bool> SignIn(string code)
        {
            var challenge = await login.HandleAsync(new LoginRequest(email!, "pw-replay", null));
            return (await verify.HandleAsync(new MfaVerifyLoginRequest(challenge.Value!.MfaToken!, code))).IsSuccess;
        }

        (await SignIn(enrollCode)).Should().BeFalse("le code de l'enrôlement a déjà servi");
        var next = Totp.Generate(secret, DateTimeOffset.UtcNow.AddSeconds(30));
        (await SignIn(next)).Should().BeTrue();
        (await SignIn(next)).Should().BeFalse("l'ancien contrôle acceptait le même code pendant toute sa fenêtre");
    }

    // ---- §8-9 : un code de récupération authentifie une seule fois ----
    [Fact]
    public async Task A_recovery_code_authenticates_once_then_is_spent()
    {
        var uid = await NewUser("pw-recov");
        string recovery;
        await using (var s = Scope())
        {
            AsUser(s.ServiceProvider, uid);
            var mfa = s.ServiceProvider.GetRequiredService<IMfaService>();
            var begin = await mfa.BeginEnrollmentAsync();
            var confirm = await mfa.ConfirmEnrollmentAsync(
                new MfaConfirmRequest(Totp.Generate(begin.Value!.Secret, DateTimeOffset.UtcNow)));
            recovery = confirm.Value!.RecoveryCodes[0];
        }

        await using var s2 = Scope();
        var login = s2.ServiceProvider.GetRequiredService<LoginHandler>();
        var verify = s2.ServiceProvider.GetRequiredService<MfaVerifyHandler>();
        var email = await Scalar<string>($"SELECT email FROM nexora.app_user WHERE id = '{uid}'");

        var challenge1 = await login.HandleAsync(new LoginRequest(email!, "pw-recov", null));
        var ok = await verify.HandleAsync(new MfaVerifyLoginRequest(challenge1.Value!.MfaToken!, recovery));
        ok.IsSuccess.Should().BeTrue(ok.Error?.Message);

        var challenge2 = await login.HandleAsync(new LoginRequest(email!, "pw-recov", null));
        var reused = await verify.HandleAsync(new MfaVerifyLoginRequest(challenge2.Value!.MfaToken!, recovery));
        reused.IsSuccess.Should().BeFalse("un code de récupération est à usage unique");

        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.mfa_recovery_code WHERE user_id = '{uid}' AND used_at IS NOT NULL"))
            .Should().Be(1);
    }

    // ---- §48 : un mauvais code est rejeté ; cinq tentatives tuent le défi ----
    [Fact]
    public async Task Five_wrong_codes_exhaust_the_challenge()
    {
        var uid = await NewUser("pw-attempts");
        await using (var s = Scope())
        {
            AsUser(s.ServiceProvider, uid);
            var mfa = s.ServiceProvider.GetRequiredService<IMfaService>();
            var begin = await mfa.BeginEnrollmentAsync();
            await mfa.ConfirmEnrollmentAsync(new MfaConfirmRequest(Totp.Generate(begin.Value!.Secret, DateTimeOffset.UtcNow)));
        }

        await using var s2 = Scope();
        var login = s2.ServiceProvider.GetRequiredService<LoginHandler>();
        var verify = s2.ServiceProvider.GetRequiredService<MfaVerifyHandler>();
        var email = await Scalar<string>($"SELECT email FROM nexora.app_user WHERE id = '{uid}'");
        var token = (await login.HandleAsync(new LoginRequest(email!, "pw-attempts", null))).Value!.MfaToken!;

        for (var i = 0; i < 5; i++)
            (await verify.HandleAsync(new MfaVerifyLoginRequest(token, "000000"))).IsSuccess.Should().BeFalse();

        var afterLockout = await verify.HandleAsync(new MfaVerifyLoginRequest(token, "000000"));
        afterLockout.IsSuccess.Should().BeFalse();
        afterLockout.Error!.Code.Should().Be("auth.mfa_too_many_attempts");
    }

    // ---- §4, §7, §48 : cinq mots de passe erronés verrouillent le compte temporairement ----
    [Fact]
    public async Task Five_bad_passwords_lock_the_account_temporarily()
    {
        var uid = await NewUser("correct-horse");
        var email = await Scalar<string>($"SELECT email FROM nexora.app_user WHERE id = '{uid}'");

        await using var s = Scope();
        var login = s.ServiceProvider.GetRequiredService<LoginHandler>();

        for (var i = 0; i < 5; i++)
            (await login.HandleAsync(new LoginRequest(email!, "wrong", null))).Error!.Code.Should().Be("auth.invalid_credentials");

        (await Scalar<DateTime?>($"SELECT locked_until FROM nexora.app_user WHERE id = '{uid}'")).Should().NotBeNull();

        // Même le bon mot de passe est refusé pendant le verrouillage.
        var locked = await login.HandleAsync(new LoginRequest(email!, "correct-horse", null));
        locked.Error!.Code.Should().Be("auth.account_locked");
    }

    // ---- §9, §39 : un administrateur réinitialise la MFA d'un utilisateur ----
    [Fact]
    public async Task An_administrator_can_reset_a_users_mfa()
    {
        var target = await NewUser("pw-target");
        // rattache la cible à la Pharmacie ABC pour partager le périmètre du directeur.
        await Sql($"""
            INSERT INTO nexora.user_role (user_id, role_id, scope_id)
            SELECT '{target}', ur.role_id, ur.scope_id FROM nexora.user_role ur
            JOIN nexora.role r ON r.id = ur.role_id AND r.name = 'cashier'
            WHERE ur.user_id = '{Cashier}' LIMIT 1
            ON CONFLICT DO NOTHING
            """);

        await using (var s = Scope())
        {
            AsUser(s.ServiceProvider, target);
            var mfa = s.ServiceProvider.GetRequiredService<IMfaService>();
            var begin = await mfa.BeginEnrollmentAsync();
            await mfa.ConfirmEnrollmentAsync(new MfaConfirmRequest(Totp.Generate(begin.Value!.Secret, DateTimeOffset.UtcNow)));
        }

        await using var s2 = Scope();
        AsUser(s2.ServiceProvider, Direction);
        var admin = s2.ServiceProvider.GetRequiredService<IMfaService>();

        var reset = await admin.ResetForUserAsync(target);
        reset.IsSuccess.Should().BeTrue(reset.Error?.Message);
        (await Scalar<bool>($"SELECT mfa_enabled FROM nexora.app_user WHERE id = '{target}'")).Should().BeFalse();
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.mfa_recovery_code WHERE user_id = '{target}'")).Should().Be(0);

        // pas de réinitialisation de soi-même par ce canal.
        AsUser(s2.ServiceProvider, Direction);
        (await s2.ServiceProvider.GetRequiredService<IMfaService>().ResetForUserAsync(Direction))
            .Error!.Code.Should().Be("security.self_reset");
    }

    // ---- Audit 2026-10, A-011 : la permission doit être détenue là où la cible travaille ----
    [Fact]
    public async Task Reset_permission_held_in_another_company_does_not_reach_a_shared_company()
    {
        var other = await Scalar<Guid>($"SELECT id FROM nexora.company WHERE id <> '{Company}' ORDER BY id LIMIT 1");
        other.Should().NotBeEmpty("le jeu de test doit compter une seconde entreprise");
        var caller = await NewUser("pw-caller");
        var target = await NewUser("pw-target-scope");
        // Directeur dans l'autre entreprise, simple caissier dans la Pharmacie ABC — comme la cible.
        await Sql($"""
            INSERT INTO nexora.access_scope (scope_type, company_id)
            SELECT 'COMPANY', '{other}' WHERE NOT EXISTS (
                SELECT 1 FROM nexora.access_scope WHERE scope_type = 'COMPANY' AND company_id = '{other}');
            INSERT INTO nexora.user_role (user_id, role_id, scope_id)
            SELECT '{caller}', r.id, sc.id FROM nexora.role r, nexora.access_scope sc
            WHERE r.name = 'owner_direction' AND r.system_role AND sc.scope_type = 'COMPANY' AND sc.company_id = '{other}'
            LIMIT 1;
            INSERT INTO nexora.user_role (user_id, role_id, scope_id)
            SELECT u.id, ur.role_id, ur.scope_id FROM nexora.user_role ur
            JOIN nexora.role r ON r.id = ur.role_id AND r.name = 'cashier'
            CROSS JOIN (VALUES ('{caller}'::uuid), ('{target}'::uuid)) AS u(id)
            WHERE ur.user_id = '{Cashier}'
            ON CONFLICT DO NOTHING;
            """);

        await using var s = Scope();
        AsUser(s.ServiceProvider, caller);
        (await s.ServiceProvider.GetRequiredService<IMfaService>().ResetForUserAsync(target))
            .Error!.Code.Should().Be("security.out_of_scope", "l'ancien contrôle se contentait d'une entreprise commune");
    }

    // ---- §37 : la politique d'entreprise signale la MFA comme requise pour un rôle sensible ----
    [Fact]
    public async Task Company_policy_flags_mfa_as_required_for_a_configured_role()
    {
        await Sql($"""
            INSERT INTO nexora.security_policy (company_id) VALUES ('{Company}') ON CONFLICT DO NOTHING;
            -- Toutes les sociétés du groupe : le rôle de groupe de la direction relève de chacune (phase 17).
            UPDATE nexora.security_policy SET mfa_required_roles = '["cashier"]'::jsonb
            WHERE company_id IN (SELECT id FROM nexora.company WHERE group_id = (SELECT group_id FROM nexora.company WHERE id = '{Company}'));
            """);
        try
        {
            await using var s = Scope();
            var mfa = s.ServiceProvider.GetRequiredService<IMfaService>();
            (await mfa.PolicyRequiresMfaAsync(Cashier)).Should().BeTrue();
            (await mfa.PolicyRequiresMfaAsync(Direction)).Should().BeFalse();
        }
        finally
        {
            await Sql($"""
                UPDATE nexora.security_policy
                SET mfa_required_roles = '["super_administrator","group_administrator","owner_direction","chief_accountant"]'::jsonb
                WHERE company_id IN (SELECT id FROM nexora.company WHERE group_id = (SELECT group_id FROM nexora.company WHERE id = '{Company}'))
                """);
        }
    }
}
