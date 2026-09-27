using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Identity;
using Nexora.Application.Notifications;
using Nexora.Application.Parties;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Notifications;
using Nexora.Contracts.Parties;
using Nexora.Infrastructure.Identity;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Recette Phase 55 — corrections des lignes FAIL de la grille : fiches clients (CLI-001/002/007),
/// fournisseurs (ACH-001), modification produit (PROD-004), création d'utilisateur (RBAC-001),
/// changement et réinitialisation de mot de passe (AUTH-007/008). Base clonée : aucun effet sur
/// la base de développement.
/// </summary>
[Collection("finance")]
public sealed class RecetteCorrectionsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Pharmacy = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Restaurant = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Cashier = Guid.Parse("dce00000-0000-0000-0000-0000000000d3");
    private static readonly Guid BoxUnit = Guid.Parse("dce00000-0000-0000-0000-00000000a011");   // « Boîte », pharmacie
    private const string StrongPassword = "Recette2026-ok";

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static T As<T>(IServiceProvider sp, Guid user, Guid company) where T : notnull
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([company], [], company);
        return sp.GetRequiredService<T>();
    }

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    private async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string UniqueEmail() => $"recette-{Guid.NewGuid():N}@test.demo";

    // ================================================================ Clients (CLI-001/002/007)

    [Fact]
    public async Task Customer_is_created_numbered_found_by_search_and_updated()
    {
        await using var s = Scope();
        var customers = As<ICustomerService>(s.ServiceProvider, Direction, Pharmacy);
        var name = $"Client recette {Guid.NewGuid():N}"[..28];

        var created = await customers.CreateAsync(new CreateCustomerRequest(name, Phone: "+509 3700 0000", Email: "client@recette.demo"));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        created.Value!.CustomerNumber.Should().MatchRegex(@"^CLI-\d{5}$");
        created.Value.Currency.Should().Be("HTG", "la devise par défaut de l'entreprise s'applique");
        created.Value.OpenReceivables.Should().Be(0);

        (await customers.ListAsync(name[..20], null)).Should().ContainSingle(c => c.Id == created.Value.Id);

        var updated = await customers.UpdateAsync(created.Value.Id, new UpdateCustomerRequest(name + " SA", "business", Address: "Delmas 33"));
        updated.IsSuccess.Should().BeTrue(updated.Error?.Message);
        updated.Value!.Type.Should().Be("business");
        updated.Value.CustomerNumber.Should().Be(created.Value.CustomerNumber, "le numéro client ne change jamais");

        (await customers.CreateAsync(new CreateCustomerRequest("", Email: "x@y.z"))).Error!.Code.Should().Be("party.name");
        (await customers.CreateAsync(new CreateCustomerRequest("Doublon", CustomerNumber: created.Value.CustomerNumber)))
            .Error!.Code.Should().Be("customer.number_exists");
    }

    [Fact]
    public async Task A_cashier_can_create_a_customer_but_never_open_credit()
    {
        await using var s = Scope();
        var customers = As<ICustomerService>(s.ServiceProvider, Cashier, Pharmacy);

        var withCredit = await customers.CreateAsync(new CreateCustomerRequest("Client à crédit", CreditLimit: 5000));
        withCredit.Error!.Code.Should().Be("customer.credit_limit_forbidden");

        var plain = await customers.CreateAsync(new CreateCustomerRequest("Client comptant"));
        plain.IsSuccess.Should().BeTrue(plain.Error?.Message);

        (await customers.UpdateAsync(plain.Value!.Id, new UpdateCustomerRequest("Client comptant", CreditLimit: 1000)))
            .Error!.Code.Should().Be("customer.credit_limit_forbidden");
    }

    [Fact]
    public async Task A_customer_of_one_company_is_invisible_from_another()
    {
        await using var s = Scope();
        var pharmacy = As<ICustomerService>(s.ServiceProvider, Direction, Pharmacy);
        var created = await pharmacy.CreateAsync(new CreateCustomerRequest("Client isolé pharmacie"));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);

        await using var s2 = Scope();
        var restaurant = As<ICustomerService>(s2.ServiceProvider, Direction, Restaurant);
        (await restaurant.GetAsync(created.Value!.Id)).Should().BeNull();
        (await restaurant.UpdateAsync(created.Value.Id, new UpdateCustomerRequest("Piratage"))).Error!.Code.Should().Be("customer.not_found");
    }

    // ============================================================= Fournisseurs (ACH-001/011)

    [Fact]
    public async Task Supplier_is_created_numbered_and_updated()
    {
        await using var s = Scope();
        var suppliers = As<ISupplierService>(s.ServiceProvider, Direction, Pharmacy);

        var created = await suppliers.CreateAsync(new CreateSupplierRequest("Grossiste recette", Email: "achat@grossiste.demo"));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        created.Value!.SupplierNumber.Should().MatchRegex(@"^FRN-\d{5}$");
        created.Value.OpenPayables.Should().Be(0);

        var updated = await suppliers.UpdateAsync(created.Value.Id, new UpdateSupplierRequest("Grossiste recette SA", Phone: "+509 2200 0000"));
        updated.IsSuccess.Should().BeTrue(updated.Error?.Message);
        updated.Value!.Phone.Should().Be("+509 2200 0000");

        (await suppliers.CreateAsync(new CreateSupplierRequest("Mauvaise devise", Currency: "XYZ"))).Error!.Code.Should().Be("supplier.currency");
        (await suppliers.UpdateAsync(Guid.NewGuid(), new UpdateSupplierRequest("Inconnu"))).Error!.Code.Should().Be("supplier.not_found");
    }

    // ===================================================================== Produit (PROD-004)

    [Fact]
    public async Task Product_details_can_be_updated_but_the_sku_never_changes()
    {
        await using var s = Scope();
        var catalog = As<CatalogService>(s.ServiceProvider, Direction, Pharmacy);
        var sku = $"RCT-{Guid.NewGuid():N}"[..14];
        var created = await catalog.CreateProductAsync(new CreateProductCommand(
            sku, "Sirop recette", null, BoxUnit, "stockable", null, null, null, null, false, false));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        var id = created.Value;

        var r = await catalog.UpdateProductAsync(id, new UpdateProductCommand(
            "Sirop recette 125 ml", "Antitussif", "SIROP 125", null, null, null, null));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);

        (await Scalar<string>($"SELECT name FROM nexora.product WHERE id = '{id}'")).Should().Be("Sirop recette 125 ml");
        (await Scalar<string>($"SELECT short_name FROM nexora.product WHERE id = '{id}'")).Should().Be("SIROP 125");
        (await Scalar<string>($"SELECT sku FROM nexora.product WHERE id = '{id}'")).Should().Be(sku);

        (await catalog.UpdateProductAsync(Guid.NewGuid(), new UpdateProductCommand("X", null, null, null, null, null, null)))
            .Error!.Code.Should().Be("catalog.product_not_found");
        (await catalog.UpdateProductAsync(id, new UpdateProductCommand(" ", null, null, null, null, null, null)))
            .Error!.Code.Should().Be("catalog.name_required");
        await using var s2 = Scope();
        var restaurantCatalog = As<CatalogService>(s2.ServiceProvider, Direction, Restaurant);
        (await restaurantCatalog.UpdateProductAsync(id, new UpdateProductCommand("Piratage", null, null, null, null, null, null)))
            .Error!.Code.Should().Be("catalog.product_not_found", "un produit d'une autre entreprise n'existe pas vu d'ici");
    }

    // ============================================================ Utilisateurs (RBAC-001/002)

    [Fact]
    public async Task A_user_is_created_with_a_first_assignment_and_can_log_in_with_its_permissions()
    {
        await using var s = Scope();
        var users = As<IUserAssignmentService>(s.ServiceProvider, Direction, Pharmacy);
        var email = UniqueEmail();

        var created = await users.CreateUserAsync(new CreateUserRequest(email, "Vendeur recette", StrongPassword,
            new GrantAssignmentRequest("salesperson", "COMPANY", Pharmacy, null, null, null, "Recrutement")));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);

        var hasher = s.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var hash = await Scalar<string>($"SELECT password_hash FROM nexora.app_user WHERE id = '{created.Value}'");
        hash.Should().NotBe(StrongPassword, "le mot de passe n'est jamais stocké en clair");
        hasher.Verify(StrongPassword, hash!).Should().BeTrue();

        var profile = await s.ServiceProvider.GetRequiredService<IPermissionService>().LoadProfileAsync(created.Value);
        profile.PermissionsFor(Pharmacy).Should().Contain("pos.sell").And.NotContain("admin.manage_users");
        (await users.ListUsersAsync()).Should().Contain(u => u.Id == created.Value);

        (await users.CreateUserAsync(new CreateUserRequest(email.ToUpperInvariant(), "Doublon", StrongPassword,
            new GrantAssignmentRequest("salesperson", "COMPANY", Pharmacy, null, null, null, null))))
            .Error!.Code.Should().Be("identity.email_exists");
    }

    [Fact]
    public async Task Creating_a_user_more_powerful_than_oneself_is_refused_and_leaves_no_account()
    {
        await using var s = Scope();
        var users = As<IUserAssignmentService>(s.ServiceProvider, Cashier, Pharmacy);
        var email = UniqueEmail();

        var r = await users.CreateUserAsync(new CreateUserRequest(email, "Directeur usurpé", StrongPassword,
            new GrantAssignmentRequest("owner_direction", "COMPANY", Pharmacy, null, null, null, null)));

        r.Error!.Code.Should().Be("identity.escalation");
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.app_user WHERE email = '{email}'"))
            .Should().Be(0, "le refus de l'affectation annule la création du compte dans la même transaction");
    }

    [Fact]
    public async Task Weak_passwords_are_refused_everywhere()
    {
        await using var s = Scope();
        var users = As<IUserAssignmentService>(s.ServiceProvider, Direction, Pharmacy);
        foreach (var weak in new[] { "court1", "sanschiffres", "1234567890" })
        {
            var r = await users.CreateUserAsync(new CreateUserRequest(UniqueEmail(), "Faible", weak,
                new GrantAssignmentRequest("salesperson", "COMPANY", Pharmacy, null, null, null, null)));
            r.Error!.Code.Should().Be("auth.password_policy", weak);
        }
    }

    // ========================================================== Mot de passe (AUTH-007/008)

    private sealed class CapturingEmailChannel : INotificationChannel
    {
        public List<OutboxMessageDto> Sent { get; } = [];
        public string Channel => "email";
        public Task<ChannelSendResult> SendAsync(OutboxMessageDto message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(ChannelSendResult.Ok());
        }
    }

    private async Task<Guid> CreateUserAsync(IServiceProvider sp, string email)
    {
        var users = As<IUserAssignmentService>(sp, Direction, Pharmacy);
        var r = await users.CreateUserAsync(new CreateUserRequest(email, "Compte mot de passe", StrongPassword,
            new GrantAssignmentRequest("salesperson", "COMPANY", Pharmacy, null, null, null, null)));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value;
    }

    [Fact]
    public async Task Changing_the_password_requires_the_current_one_and_ends_every_session()
    {
        await using var s = Scope();
        var email = UniqueEmail();
        var userId = await CreateUserAsync(s.ServiceProvider, email);
        var epochBefore = await Scalar<DateTime>($"SELECT security_epoch FROM nexora.app_user WHERE id = '{userId}'");

        var accounts = As<IAccountService>(s.ServiceProvider, userId, Pharmacy);
        (await accounts.ChangePasswordAsync(new ChangePasswordRequest("mauvais-mdp-1", "Nouveau2026-ok")))
            .Error!.Code.Should().Be("auth.current_password");
        (await accounts.ChangePasswordAsync(new ChangePasswordRequest(StrongPassword, StrongPassword)))
            .Error!.Code.Should().Be("auth.password_reuse");

        var ok = await accounts.ChangePasswordAsync(new ChangePasswordRequest(StrongPassword, "Nouveau2026-ok"));
        ok.IsSuccess.Should().BeTrue(ok.Error?.Message);

        var hasher = s.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var hash = await Scalar<string>($"SELECT password_hash FROM nexora.app_user WHERE id = '{userId}'");
        hasher.Verify("Nouveau2026-ok", hash!).Should().BeTrue();
        hasher.Verify(StrongPassword, hash!).Should().BeFalse();
        (await Scalar<DateTime>($"SELECT security_epoch FROM nexora.app_user WHERE id = '{userId}'"))
            .Should().BeAfter(epochBefore, "tous les jetons émis avant le changement deviennent invalides");
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.audit_log WHERE user_id = '{userId}' AND action = 'password.changed'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Password_reset_is_emailed_single_use_and_never_reveals_whether_an_account_exists()
    {
        await using var s = Scope();
        var email = UniqueEmail();
        var userId = await CreateUserAsync(s.ServiceProvider, email);
        await Sql($"UPDATE nexora.app_user SET failed_login_count = 5, locked_until = now() + interval '10 minutes' WHERE id = '{userId}'");

        var mail = new CapturingEmailChannel();
        var accounts = ActivatorUtilities.CreateInstance<SqlAccountService>(s.ServiceProvider, (IEnumerable<INotificationChannel>)[mail]);

        // Compte inconnu : aucune erreur, aucun email, aucune ligne — même réponse que pour un compte réel.
        await accounts.RequestPasswordResetAsync(new ForgotPasswordRequest("inconnu@nulle-part.demo"));
        mail.Sent.Should().BeEmpty();

        await accounts.RequestPasswordResetAsync(new ForgotPasswordRequest(email.ToUpperInvariant()));
        var message = mail.Sent.Should().ContainSingle().Which;
        message.Address.Should().Be(email);
        var token = message.Body.Split('\n').Single(l => l.Length == 64 && l.All(Uri.IsHexDigit));
        (await Scalar<string>($"SELECT token_hash FROM nexora.password_reset WHERE user_id = '{userId}'"))
            .Should().NotBe(token, "seul le hachage du code est stocké");

        (await accounts.ResetPasswordAsync(new ResetPasswordRequest(token, "court"))).Error!.Code.Should().Be("auth.password_policy");
        var reset = await accounts.ResetPasswordAsync(new ResetPasswordRequest(token, "Retrouve2026-ok"));
        reset.IsSuccess.Should().BeTrue(reset.Error?.Message);

        var hasher = s.ServiceProvider.GetRequiredService<IPasswordHasher>();
        hasher.Verify("Retrouve2026-ok", (await Scalar<string>($"SELECT password_hash FROM nexora.app_user WHERE id = '{userId}'"))!)
            .Should().BeTrue();
        (await Scalar<DateTime?>($"SELECT locked_until FROM nexora.app_user WHERE id = '{userId}'"))
            .Should().BeNull("une réinitialisation réussie lève le verrouillage anti-force-brute");

        (await accounts.ResetPasswordAsync(new ResetPasswordRequest(token, "Encore2026-ok")))
            .Error!.Code.Should().Be("auth.reset_token", "un code ne sert qu'une fois");
    }

    [Fact]
    public async Task Reset_requests_are_throttled_and_expired_codes_are_refused()
    {
        await using var s = Scope();
        var email = UniqueEmail();
        var userId = await CreateUserAsync(s.ServiceProvider, email);
        var mail = new CapturingEmailChannel();
        var accounts = ActivatorUtilities.CreateInstance<SqlAccountService>(s.ServiceProvider, (IEnumerable<INotificationChannel>)[mail]);

        for (var i = 0; i < SqlAccountService.MaxRequestsPerHour + 2; i++)
            await accounts.RequestPasswordResetAsync(new ForgotPasswordRequest(email));
        mail.Sent.Should().HaveCount(SqlAccountService.MaxRequestsPerHour);

        var token = mail.Sent[0].Body.Split('\n').Single(l => l.Length == 64 && l.All(Uri.IsHexDigit));
        await Sql($"UPDATE nexora.password_reset SET expires_at = now() - interval '1 minute' WHERE user_id = '{userId}'");
        (await accounts.ResetPasswordAsync(new ResetPasswordRequest(token, "Expire2026-ok"))).Error!.Code.Should().Be("auth.reset_token");
    }
}
