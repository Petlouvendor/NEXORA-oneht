using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Preferences;
using Nexora.Contracts.Preferences;

namespace Nexora.IntegrationTests;

/// <summary>Scénarios de validation UX — préférences utilisateur & personnalisation entreprise (Phase 13 §35-36).</summary>
[Collection("finance")]
public sealed class UxPreferencesTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid CompanyResto = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid ChiefAccountant = Guid.Parse("dce00000-0000-0000-0000-0000000000d2");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    // ---- §36 : préférences par défaut + aller-retour + fusion partielle ----
    [Fact]
    public async Task User_preferences_default_then_round_trip_with_partial_merge()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IUserPreferenceService>();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Guid.NewGuid());   // utilisateur sans préférence enregistrée

        var def = await svc.GetAsync();
        def.Theme.Should().Be("system");
        def.Density.Should().Be("comfortable");

        tc.SetUser(Direction);
        var saved = await svc.UpdateAsync(new UpdateUserPreferenceRequest(
            "dark", "compact", "fr-FR", true, "detailed", ["/pos", "/finance"], null, null));
        saved.IsSuccess.Should().BeTrue(saved.Error?.Message);
        saved.Value!.Theme.Should().Be("dark");
        saved.Value.Favorites.Should().Equal("/pos", "/finance");

        // Mise à jour partielle : seule la langue change, le reste est conservé.
        var merged = await svc.UpdateAsync(new UpdateUserPreferenceRequest(
            null, null, "en-US", null, null, null, null, null));
        merged.Value!.Theme.Should().Be("dark");
        merged.Value.Density.Should().Be("compact");
        merged.Value.Locale.Should().Be("en-US");
        merged.Value.ReducedMotion.Should().BeTrue();
    }

    // ---- §36 : chaque utilisateur ne voit et ne modifie que ses préférences ----
    [Fact]
    public async Task User_preferences_are_isolated_per_user()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IUserPreferenceService>();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();

        tc.SetUser(Direction);
        await svc.UpdateAsync(new UpdateUserPreferenceRequest("dark", null, null, null, null, null, null, null));

        tc.SetUser(ChiefAccountant);
        await svc.UpdateAsync(new UpdateUserPreferenceRequest("light", null, null, null, null, null, null, null));
        (await svc.GetAsync()).Theme.Should().Be("light");

        tc.SetUser(Direction);
        (await svc.GetAsync()).Theme.Should().Be("dark");
    }

    // ---- §36 : valeurs hors référentiel refusées ----
    [Fact]
    public async Task Invalid_preference_values_are_rejected()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IUserPreferenceService>();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);

        (await svc.UpdateAsync(new UpdateUserPreferenceRequest("neon", null, null, null, null, null, null, null)))
            .IsSuccess.Should().BeFalse();
        (await svc.UpdateAsync(new UpdateUserPreferenceRequest(null, "ultra", null, null, null, null, null, null)))
            .IsSuccess.Should().BeFalse();
        (await svc.UpdateAsync(new UpdateUserPreferenceRequest(null, null, null, null, "3d", null, null, null)))
            .IsSuccess.Should().BeFalse();
    }

    // ---- §35 : personnalisation d'entreprise, bornée au périmètre ----
    [Fact]
    public async Task Company_branding_round_trips_and_is_scoped_to_the_company()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<ICompanyBrandingService>();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();

        tc.SetScope([Company], [], Company);
        var pharma = await svc.UpdateAsync(new UpdateCompanyBrandingRequest(
            "Pharmacie ABC — Delmas", "#00E0C0", null, "standard", "Merci", null, null, null));
        pharma.IsSuccess.Should().BeTrue(pharma.Error?.Message);
        pharma.Value!.CompanyId.Should().Be(Company);

        tc.SetScope([CompanyResto], [], CompanyResto);
        var resto = await svc.UpdateAsync(new UpdateCompanyBrandingRequest(
            "Resto ABC — Pétionville", "#2FBF71", null, "detailed", null, null, null, null));
        resto.Value!.DisplayName.Should().Be("Resto ABC — Pétionville");
        resto.Value.AccentColor.Should().Be("#2FBF71");
        resto.Value.AccentColor.Should().NotBe(pharma.Value.AccentColor);

        tc.SetScope([Company], [], Company);
        var pharmaAgain = await svc.GetAsync();
        pharmaAgain.AccentColor.Should().Be("#00E0C0");        // la Pharmacie n'a pas bougé
        pharmaAgain.DisplayName.Should().Be("Pharmacie ABC — Delmas");
    }

    // ---- §35 : un accent qui casserait le contraste est refusé ----
    [Fact]
    public async Task Accent_color_that_would_hurt_accessibility_is_rejected()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<ICompanyBrandingService>();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetScope([CompanyResto], [], CompanyResto);

        (await svc.UpdateAsync(new UpdateCompanyBrandingRequest(null, "#000000", null, null, null, null, null, null)))
            .IsSuccess.Should().BeFalse();
        (await svc.UpdateAsync(new UpdateCompanyBrandingRequest(null, "#FFFFFF", null, null, null, null, null, null)))
            .IsSuccess.Should().BeFalse();
        (await svc.UpdateAsync(new UpdateCompanyBrandingRequest(null, "pas-une-couleur", null, null, null, null, null, null)))
            .IsSuccess.Should().BeFalse();
        (await svc.UpdateAsync(new UpdateCompanyBrandingRequest(null, "#3AA0FF", null, null, null, null, null, null)))
            .IsSuccess.Should().BeTrue();
    }

    // ---- §36 : colonnes de tableau et préférences de notification (jsonb) persistées ----
    [Fact]
    public async Task Table_column_and_notification_preferences_persist()
    {
        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<IUserPreferenceService>();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);

        var r = await svc.UpdateAsync(new UpdateUserPreferenceRequest(
            null, null, null, null, null, null,
            Json("""{"sales":["date","total","status"]}"""),
            Json("""{"critical":true,"info":false}""")));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);

        var back = await svc.GetAsync();
        back.TableColumns.GetProperty("sales")[0].GetString().Should().Be("date");
        back.NotificationPrefs.GetProperty("critical").GetBoolean().Should().BeTrue();
    }
}
