using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Identity;
using Nexora.Application.Security;
using Nexora.Contracts.Auth;
using Nexora.Contracts.Security;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation phase 17 — réauthentification forte et double authentification : la règle « rôles soumis à la
/// double authentification » couvre enfin les rôles attribués au groupe ; une entreprise peut l'imposer pour
/// les actions critiques ; si la double authentification est active, son code est exigé.
/// </summary>
[Collection("finance")]
public sealed class StepUpMfaTests(FinanceEngineFixture fx)
{
    private static readonly Guid Pharmacy = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");

    [Fact]
    public async Task Strong_confirmation_requires_the_second_factor_and_honours_the_policy()
    {
        await using var s = fx.Provider.CreateAsyncScope();
        var sp = s.ServiceProvider;
        var db = sp.GetRequiredService<NexoraDbContext>();
        var mfa = sp.GetRequiredService<IMfaService>();
        var stepUp = sp.GetRequiredService<IStepUpAuthService>();
        var password = "Step!Up2026";
        var originalHash = await db.Database.SqlQuery<string>($"""
            SELECT password_hash AS "Value" FROM nexora.app_user WHERE id = {fx.UserId}
            """).FirstAsync();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.app_user SET password_hash = {sp.GetRequiredService<Nexora.Application.Abstractions.IPasswordHasher>().Hash(password)},
                mfa_enabled = false, mfa_secret = NULL, mfa_pending_secret = NULL
            WHERE id = {fx.UserId}
            """);
        await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.step_up_failure WHERE user_id = {fx.UserId}");

        // La direction tient son rôle au niveau du groupe : elle échappait à la règle.
        (await mfa.PolicyRequiresMfaAsync(fx.UserId)).Should().BeTrue("un rôle de groupe relève de la politique de ses sociétés");

        // Réglage par défaut (phase 47) : indicatif — la confirmation par mot de passe suffit.
        (await stepUp.ChallengeAsync(new StepUpChallengeRequest(password, "payment"))).IsSuccess.Should().BeTrue();

        try
        {
            await db.Database.ExecuteSqlAsync($"UPDATE nexora.security_policy SET enforce_mfa_for_critical = true WHERE company_id = {Pharmacy}");
            (await stepUp.ChallengeAsync(new StepUpChallengeRequest(password, "payment"))).Error!.Code
                .Should().Be("security.mfa_enrollment_required");

            var enrollment = (await mfa.BeginEnrollmentAsync()).Value!;
            (await mfa.ConfirmEnrollmentAsync(new MfaConfirmRequest(Totp.Generate(enrollment.Secret, DateTimeOffset.UtcNow)))).IsSuccess.Should().BeTrue();

            (await stepUp.ChallengeAsync(new StepUpChallengeRequest(password, "payment"))).Error!.Code.Should().Be("security.step_up_code_required");
            (await stepUp.ChallengeAsync(new StepUpChallengeRequest(password, "payment", "000000"))).Error!.Code.Should().Be("security.step_up_failed");
            var granted = await stepUp.ChallengeAsync(new StepUpChallengeRequest(password, "payment", Totp.Generate(enrollment.Secret, DateTimeOffset.UtcNow)));
            granted.IsSuccess.Should().BeTrue(granted.Error?.Message);
            (await db.Database.SqlQuery<string>($"""
                SELECT method AS "Value" FROM nexora.step_up_challenge WHERE user_id = {fx.UserId} ORDER BY issued_at DESC LIMIT 1
                """).FirstAsync()).Should().Be("password+mfa");

            // Valable pour sa catégorie seulement.
            (await stepUp.ValidateAsync(granted.Value!.Token, "payment", null)).Should().BeTrue();
            (await stepUp.ValidateAsync(granted.Value.Token, "permission_change", null)).Should().BeFalse();
        }
        finally
        {
            await db.Database.ExecuteSqlAsync($"UPDATE nexora.security_policy SET enforce_mfa_for_critical = false WHERE company_id = {Pharmacy}");
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.app_user SET mfa_enabled = false, mfa_secret = NULL, mfa_pending_secret = NULL,
                    password_hash = {originalHash} WHERE id = {fx.UserId}
                """);
            await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.step_up_failure WHERE user_id = {fx.UserId}");
            await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.mfa_recovery_code WHERE user_id = {fx.UserId}");
        }
    }

    [Fact]
    public async Task Security_policy_bounds_and_enforcement_switch()
    {
        await using var s = fx.Provider.CreateAsyncScope();
        var policy = s.ServiceProvider.GetRequiredService<ISecurityPolicyService>();
        (await policy.SetAsync(new SetSecurityPolicyRequest(null, 5000, null, null, null, null))).Error!.Code.Should().Be("security.ttl");
        (await policy.SetAsync(new SetSecurityPolicyRequest(null, null, 600, null, null, null))).Error!.Code.Should().Be("security.ttl");
        (await policy.SetAsync(new SetSecurityPolicyRequest(null, null, null, null, null, null, true))).IsSuccess.Should().BeTrue();
        (await policy.GetAsync()).EnforceMfaForCritical.Should().BeTrue();
        (await policy.SetAsync(new SetSecurityPolicyRequest(null, null, null, null, null, null, false))).IsSuccess.Should().BeTrue();
    }
}
