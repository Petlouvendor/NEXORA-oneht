using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Integrations;
using Nexora.Contracts.Integrations;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>Plateforme ouverte — clés d'API : création, périmètre borné, résolution, rotation (Phase 35 §13-14, §54).</summary>
[Collection("finance")]
public sealed class ApiKeyPlatformTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    private static void AsDirection(IServiceProvider sp)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
    }

    private static async Task<string> AnOwnedPermission(IServiceProvider sp)
    {
        var profile = await sp.GetRequiredService<IPermissionService>().LoadProfileAsync(Direction);
        return profile.PermissionsFor(Company).First();
    }

    // ---- §14 : le secret n'est renvoyé qu'à la création, jamais par la liste ----
    [Fact]
    public async Task Create_returns_the_secret_once_and_the_list_never_does()
    {
        await using var s = Scope();
        AsDirection(s.ServiceProvider);
        var keys = s.ServiceProvider.GetRequiredService<IApiKeyService>();
        var scope = await AnOwnedPermission(s.ServiceProvider);

        var created = await keys.CreateAsync(new CreateApiKeyRequest("Connecteur e-commerce", [scope]));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        created.Value!.Secret.Should().StartWith("nxk_live_").And.Contain(created.Value.Key.KeyPrefix);
        created.Value.Key.Status.Should().Be("active");

        var listed = await keys.ListAsync();
        listed.Should().ContainSingle(k => k.Id == created.Value.Key.Id);
        // le DTO de liste n'a pas de champ secret — vérifie que la base ne stocke que l'empreinte.
        (await Scalar<string>($"SELECT key_hash FROM nexora.api_key WHERE id = '{created.Value.Key.Id}'"))
            .Should().NotBe(created.Value.Secret).And.HaveLength(64);
    }

    // ---- §13 : une clé n'accorde jamais plus que ce que détient son propriétaire ----
    [Fact]
    public async Task A_scope_outside_the_owner_permissions_is_refused()
    {
        await using var s = Scope();
        AsDirection(s.ServiceProvider);
        var keys = s.ServiceProvider.GetRequiredService<IApiKeyService>();

        var res = await keys.CreateAsync(new CreateApiKeyRequest("Trop large", ["totally.made.up.permission"]));

        res.IsSuccess.Should().BeFalse();
        res.Error!.Code.Should().Be("api_key.scope_excess");
    }

    // ---- §13 : l'authentificateur résout une clé valide avec ses scopes ; horodate l'usage ----
    [Fact]
    public async Task The_authenticator_resolves_a_valid_key_and_stamps_last_used()
    {
        await using var s = Scope();
        AsDirection(s.ServiceProvider);
        var keys = s.ServiceProvider.GetRequiredService<IApiKeyService>();
        var scope = await AnOwnedPermission(s.ServiceProvider);
        var created = await keys.CreateAsync(new CreateApiKeyRequest("Résolution", [scope], RateLimitPerMinute: 77));

        var auth = s.ServiceProvider.GetRequiredService<IApiKeyAuthenticator>();
        var r = await auth.ResolveAsync(created.Value!.Secret, "203.0.113.9");

        r.Outcome.Should().Be(ApiKeyOutcome.Ok);
        r.CompanyId.Should().Be(Company);
        r.OwnerUserId.Should().Be(Direction);
        r.Scopes.Should().Equal(scope);
        r.RateLimitPerMinute.Should().Be(77);

        (await Scalar<DateTime?>($"SELECT last_used_at FROM nexora.api_key WHERE id = '{created.Value.Key.Id}'")).Should().NotBeNull();

        // une chaîne quelconque ou un mauvais secret ne résout rien
        (await auth.ResolveAsync("nxk_live_deadbeef_notarealsecret", null)).Outcome.Should().Be(ApiKeyOutcome.Invalid);
        (await auth.ResolveAsync("Bearer eyJ...", null)).Outcome.Should().Be(ApiKeyOutcome.Invalid);
    }

    // ---- §14 : révocation → la clé ne résout plus ----
    [Fact]
    public async Task A_revoked_key_no_longer_resolves()
    {
        await using var s = Scope();
        AsDirection(s.ServiceProvider);
        var keys = s.ServiceProvider.GetRequiredService<IApiKeyService>();
        var auth = s.ServiceProvider.GetRequiredService<IApiKeyAuthenticator>();
        var created = await keys.CreateAsync(new CreateApiKeyRequest("À révoquer", [await AnOwnedPermission(s.ServiceProvider)]));

        (await auth.ResolveAsync(created.Value!.Secret, null)).Outcome.Should().Be(ApiKeyOutcome.Ok);
        (await keys.RevokeAsync(created.Value.Key.Id)).IsSuccess.Should().BeTrue();
        (await auth.ResolveAsync(created.Value.Secret, null)).Outcome.Should().Be(ApiKeyOutcome.Revoked);

        (await keys.RevokeAsync(created.Value.Key.Id)).Error!.Code.Should().Be("api_key.not_found");
    }

    // ---- §14 : expiration → la clé ne résout plus ----
    [Fact]
    public async Task An_expired_key_no_longer_resolves()
    {
        await using var s = Scope();
        AsDirection(s.ServiceProvider);
        var keys = s.ServiceProvider.GetRequiredService<IApiKeyService>();
        var auth = s.ServiceProvider.GetRequiredService<IApiKeyAuthenticator>();
        var created = await keys.CreateAsync(new CreateApiKeyRequest("Courte durée", [await AnOwnedPermission(s.ServiceProvider)], ExpiresInDays: 30));

        await using (var c = new NpgsqlConnection(fx.TestConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"UPDATE nexora.api_key SET expires_at = now() - interval '1 day' WHERE id = '{created.Value!.Key.Id}'", c);
            await cmd.ExecuteNonQueryAsync();
        }

        (await auth.ResolveAsync(created.Value!.Secret, null)).Outcome.Should().Be(ApiKeyOutcome.Expired);
    }

    // ---- §14 : rotation → l'ancien secret meurt, un nouveau est émis avec le même périmètre ----
    [Fact]
    public async Task Rotation_kills_the_old_secret_and_issues_a_new_one()
    {
        await using var s = Scope();
        AsDirection(s.ServiceProvider);
        var keys = s.ServiceProvider.GetRequiredService<IApiKeyService>();
        var auth = s.ServiceProvider.GetRequiredService<IApiKeyAuthenticator>();
        var scope = await AnOwnedPermission(s.ServiceProvider);
        var original = await keys.CreateAsync(new CreateApiKeyRequest("Rotation", [scope]));

        var rotated = await keys.RotateAsync(original.Value!.Key.Id);
        rotated.IsSuccess.Should().BeTrue(rotated.Error?.Message);
        rotated.Value!.Secret.Should().NotBe(original.Value.Secret);
        rotated.Value.Key.Scopes.Should().Equal(scope);

        (await auth.ResolveAsync(original.Value.Secret, null)).Outcome.Should().Be(ApiKeyOutcome.Revoked);
        (await auth.ResolveAsync(rotated.Value.Secret, null)).Outcome.Should().Be(ApiKeyOutcome.Ok);
        (await Scalar<Guid?>($"SELECT rotated_from FROM nexora.api_key WHERE id = '{rotated.Value.Key.Id}'"))
            .Should().Be(original.Value.Key.Id);
    }
}
