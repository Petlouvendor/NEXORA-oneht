using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Integrations;
using Nexora.Application.Marketplace;
using Nexora.Contracts.Integrations;
using Nexora.Contracts.Marketplace;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// API, intégrations & écosystème — marketplace partenaires (Phase 46 §41-48, §56). Le catalogue
/// est curé par NEXORA (revue humaine, jamais une auto-approbation) ; une entreprise n'accorde
/// jamais plus que ce qu'une app a demandé ; une clé émise pour une app installée reste bornée au
/// périmètre accordé, en plus des permissions de son propriétaire (Phase 35 §13, inchangée).
/// </summary>
[Collection("finance")]
public sealed class MarketplaceEcosystemTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid PlatformAdmin = Guid.Parse("dce00000-0000-0000-0000-0000000000d9");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static void AsDirection(IServiceProvider sp)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
    }

    private static void AsPlatformStaff(IServiceProvider sp)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(PlatformAdmin);
    }

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    private async Task<Guid> SeededAppId(string slug) => await Scalar<Guid>($"SELECT id FROM nexora.marketplace_app WHERE slug = '{slug}'");

    // ---- §45 : une revue exige un tiers, un refus est final --------------------
    [Fact]
    public async Task Submit_requires_review_reviewer_cannot_be_submitter_and_reject_is_final()
    {
        await using var s = Scope();
        var slug = $"test-app-{Guid.NewGuid():N}"[..24];
        AsDirection(s.ServiceProvider);
        var catalog = s.ServiceProvider.GetRequiredService<IMarketplaceCatalogService>();

        var submitted = await catalog.SubmitAsync(new SubmitAppRequest(
            slug, "App de test", "Éditeur Test", null, "crm", "1.0", "Une app de démonstration.",
            ["catalog.view"]));
        submitted.IsSuccess.Should().BeTrue(submitted.Error?.Message);
        submitted.Value!.Status.Should().Be("pending_review");

        // le soumissionnaire ne peut pas réviser sa propre soumission (§45)
        var selfReview = await catalog.ReviewAsync(submitted.Value.Id, new ReviewAppRequest("approve", null));
        selfReview.IsSuccess.Should().BeFalse();
        selfReview.Error!.Code.Should().Be("marketplace.self_review");

        // un tiers (personnel plateforme) tranche — ici un refus
        AsPlatformStaff(s.ServiceProvider);
        var reviewed = await catalog.ReviewAsync(submitted.Value.Id, new ReviewAppRequest("reject", "Documentation insuffisante."));
        reviewed.IsSuccess.Should().BeTrue(reviewed.Error?.Message);
        reviewed.Value!.Status.Should().Be("rejected");
        reviewed.Value.ReviewNotes.Should().Be("Documentation insuffisante.");

        // une décision déjà tranchée ne peut être rejouée
        var again = await catalog.ReviewAsync(submitted.Value.Id, new ReviewAppRequest("approve", null));
        again.Error!.Code.Should().Be("marketplace.not_pending");
    }

    // ---- §41, §11 : catalogue public = approved uniquement, pagination par curseur ----
    [Fact]
    public async Task Approved_apps_are_publicly_listed_and_cursor_pagination_advances_without_duplicates()
    {
        await using var s = Scope();
        AsDirection(s.ServiceProvider);
        var catalog = s.ServiceProvider.GetRequiredService<IMarketplaceCatalogService>();

        var publicView = await catalog.ListAsync(null, "pending_review", null, 50, includeAllStatuses: false);
        publicView.Items.Should().OnlyContain(a => a.Status == "approved"); // le filtre de statut est ignoré pour un appelant non-staff

        var page1 = await catalog.ListAsync(null, null, null, 1, includeAllStatuses: false);
        page1.Items.Should().HaveCount(1);
        page1.NextCursor.Should().NotBeNull();

        var page2 = await catalog.ListAsync(null, null, page1.NextCursor, 50, includeAllStatuses: false);
        page2.Items.Should().NotContain(a => a.Id == page1.Items[0].Id);
    }

    // ---- §44, §56 : le périmètre accordé ne peut jamais dépasser ce que l'app a demandé --------
    [Fact]
    public async Task Install_enforces_granted_scopes_subset_of_requested_and_is_idempotent()
    {
        await using var s = Scope();
        var appId = await SeededAppId("nexora-export-comptable");
        AsDirection(s.ServiceProvider);
        var installs = s.ServiceProvider.GetRequiredService<IAppInstallationService>();

        var excess = await installs.InstallAsync(appId, new InstallAppRequest(["accounting.view", "admin.manage_users"], null));
        excess.IsSuccess.Should().BeFalse();
        excess.Error!.Code.Should().Be("marketplace.scope_excess");

        var ok = await installs.InstallAsync(appId, new InstallAppRequest(["accounting.view"], null));
        ok.IsSuccess.Should().BeTrue(ok.Error?.Message);
        ok.Value!.Status.Should().Be("installed");
        ok.Value.GrantedScopes.Should().Equal("accounting.view");

        // réinstaller la même app pour la même entreprise met à jour la ligne existante (§43) —
        // jamais une seconde ligne.
        var reinstalled = await installs.InstallAsync(appId, new InstallAppRequest(["accounting.view", "accounting.export"], null));
        reinstalled.Value!.GrantedScopes.Should().BeEquivalentTo(["accounting.view", "accounting.export"]);
        (await Scalar<long>($"SELECT count(*) FROM nexora.app_installation WHERE company_id = '{Company}' AND marketplace_app_id = '{appId}'"))
            .Should().Be(1);
    }

    // ---- §8, §46 : une clé émise pour une app reste bornée au périmètre ACCORDÉ, pas demandé ----
    [Fact]
    public async Task Api_key_for_an_installation_is_bounded_by_granted_scopes_and_owner_permissions()
    {
        await using var s = Scope();
        var appId = await SeededAppId("nexora-mobile-money-connect"); // requested_scopes = ["integrations.payments"]
        AsDirection(s.ServiceProvider);
        var installs = s.ServiceProvider.GetRequiredService<IAppInstallationService>();
        var keys = s.ServiceProvider.GetRequiredService<IApiKeyService>();

        var installed = await installs.InstallAsync(appId, new InstallAppRequest(["integrations.payments"], null));
        installed.IsSuccess.Should().BeTrue(installed.Error?.Message);

        var tooWide = await keys.CreateAsync(new CreateApiKeyRequest(
            "Clé app paiement", ["integrations.payments", "platform.manage_customers"], AppInstallationId: installed.Value!.Id));
        tooWide.IsSuccess.Should().BeFalse();
        tooWide.Error!.Code.Should().Be("api_key.scope_excess"); // d'abord borné par les permissions du propriétaire

        var scoped = await keys.CreateAsync(new CreateApiKeyRequest(
            "Clé app paiement", ["integrations.payments"], AppInstallationId: installed.Value.Id));
        scoped.IsSuccess.Should().BeTrue(scoped.Error?.Message);
        scoped.Value!.Key.AppInstallationId.Should().Be(installed.Value.Id);
    }

    // ---- §46 : désinstaller une app révoque toute clé qui lui avait été émise ------------------
    [Fact]
    public async Task Uninstall_revokes_keys_issued_for_that_installation()
    {
        await using var s = Scope();
        var appId = await SeededAppId("nexora-ecommerce-sync"); // requested_scopes = catalog/inventory/pos.manage_order
        AsDirection(s.ServiceProvider);
        var installs = s.ServiceProvider.GetRequiredService<IAppInstallationService>();
        var keys = s.ServiceProvider.GetRequiredService<IApiKeyService>();

        var installed = await installs.InstallAsync(appId, new InstallAppRequest(["catalog.view"], null));
        var key = await keys.CreateAsync(new CreateApiKeyRequest("Clé e-commerce", ["catalog.view"], AppInstallationId: installed.Value!.Id));
        key.IsSuccess.Should().BeTrue(key.Error?.Message);

        var uninstalled = await installs.UninstallAsync(appId);
        uninstalled.IsSuccess.Should().BeTrue(uninstalled.Error?.Message);

        var afterList = await keys.ListAsync();
        afterList.Should().ContainSingle(k => k.Id == key.Value!.Key.Id).Which.Status.Should().Be("revoked");

        // désinstaller une app déjà désinstallée est refusé — pas d'effet silencieux
        (await installs.UninstallAsync(appId)).Error!.Code.Should().Be("marketplace.installation_not_found");
    }

    // ---- §9, §47-48 : l'usage quotidien s'agrège par clé, pour le portail développeur ----------
    [Fact]
    public async Task Usage_recorder_aggregates_requests_and_rate_limited_hits_per_day()
    {
        await using var s = Scope();
        AsDirection(s.ServiceProvider);
        var keys = s.ServiceProvider.GetRequiredService<IApiKeyService>();
        var created = await keys.CreateAsync(new CreateApiKeyRequest("Clé mesurée", ["catalog.view"]));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);

        var recorder = s.ServiceProvider.GetRequiredService<IApiKeyUsageRecorder>();
        await recorder.RecordAsync(created.Value!.Key.Id, Company, rateLimited: false);
        await recorder.RecordAsync(created.Value.Key.Id, Company, rateLimited: false);
        await recorder.RecordAsync(created.Value.Key.Id, Company, rateLimited: true);

        var requestCount = await Scalar<int>($"""
            SELECT request_count FROM nexora.api_key_daily_usage
            WHERE api_key_id = '{created.Value.Key.Id}' AND usage_date = current_date
            """);
        var rateLimitedCount = await Scalar<int>($"""
            SELECT rate_limited_count FROM nexora.api_key_daily_usage
            WHERE api_key_id = '{created.Value.Key.Id}' AND usage_date = current_date
            """);
        requestCount.Should().Be(3);
        rateLimitedCount.Should().Be(1);

        var usage = s.ServiceProvider.GetRequiredService<IDeveloperUsageQuery>();
        var report = await usage.UsageAsync(30);
        report.Should().ContainSingle(u => u.ApiKeyId == created.Value.Key.Id)
            .Which.Daily.Should().ContainSingle(d => d.Date == DateOnly.FromDateTime(DateTime.UtcNow))
            .Which.RequestCount.Should().Be(3);
    }
}
