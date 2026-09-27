using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Preferences;
using Nexora.Contracts.Preferences;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// UX, design system &amp; accessibilité (Phase 40 §13, §21-22, §31-33, §37, §50, §58) :
/// préférences d'accessibilité, dispositions de tableau de bord (perso / défaut d'entreprise),
/// filtres sauvegardés (privé isolé / partagé visible), raccourcis, version du design system.
/// </summary>
[Collection("finance")]
public sealed class UxPersonalizationTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid Compta = Guid.Parse("dce00000-0000-0000-0000-0000000000d2");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static T For<T>(IServiceProvider sp, Guid user) where T : notnull
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(user);
        tc.SetScope([Company], [], Company);
        return sp.GetRequiredService<T>();
    }

    private async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    // ---- §31-34 : préférences d'accessibilité persistées + bornes ----
    [Fact]
    public async Task Preference_accessibility_fields_round_trip_and_bounds()
    {
        await using var s = Scope();
        var prefs = For<IUserPreferenceService>(s.ServiceProvider, Direction);

        var saved = await prefs.UpdateAsync(new UpdateUserPreferenceRequest(
            null, null, null, null, null, null, null, null,
            HighContrast: true, TextScale: 1.25m, Timezone: "America/New_York"));
        saved.IsSuccess.Should().BeTrue(saved.Error?.Message);
        saved.Value!.HighContrast.Should().BeTrue();
        saved.Value.TextScale.Should().Be(1.25m);
        saved.Value.Timezone.Should().Be("America/New_York");

        (await prefs.GetAsync()).HighContrast.Should().BeTrue();

        (await prefs.UpdateAsync(new UpdateUserPreferenceRequest(null, null, null, null, null, null, null, null, TextScale: 3.0m)))
            .Error!.Code.Should().Be("preferences.text_scale");
    }

    // ---- §13, §37 : ma disposition perso est la disposition effective ----
    [Fact]
    public async Task Dashboard_layout_personal_save_becomes_effective()
    {
        await Sql($"DELETE FROM nexora.dashboard_layout WHERE company_id = '{Company}' AND user_id = '{Direction}'");
        await using var s = Scope();
        var layouts = For<IDashboardLayoutService>(s.ServiceProvider, Direction);

        var save = await layouts.SaveAsync(new SaveDashboardLayoutRequest(
            null, "Direction perso", Json("""[{"key":"sales_today","x":0,"y":0,"w":6,"h":2}]"""), IsDefault: true));
        save.IsSuccess.Should().BeTrue(save.Error?.Message);

        var effective = await layouts.GetEffectiveAsync();
        effective!.Name.Should().Be("Direction perso");
        effective.UserId.Should().Be(Direction);
        effective.Widgets.GetArrayLength().Should().Be(1);
    }

    // ---- §37 : à défaut de disposition perso, le défaut d'entreprise s'applique ----
    [Fact]
    public async Task Dashboard_company_default_applies_without_a_personal_layout()
    {
        await Sql($"DELETE FROM nexora.dashboard_layout WHERE company_id = '{Company}'");
        await using var s = Scope();
        var admin = For<IDashboardLayoutService>(s.ServiceProvider, Direction);
        (await admin.SaveCompanyDefaultAsync(new SaveDashboardLayoutRequest(
            null, "Défaut entreprise", Json("""[{"key":"cash","x":0,"y":0,"w":4,"h":2}]"""), AsCompanyDefault: true))).IsSuccess.Should().BeTrue();

        var forCompta = For<IDashboardLayoutService>(s.ServiceProvider, Compta);
        var effective = await forCompta.GetEffectiveAsync();
        effective!.Name.Should().Be("Défaut entreprise");
        effective.UserId.Should().BeNull();
    }

    // ---- §21-22 : un filtre privé est isolé, un filtre partagé est visible des collègues ----
    [Fact]
    public async Task Saved_filter_private_is_isolated_and_shared_is_visible()
    {
        await Sql($"DELETE FROM nexora.saved_filter WHERE view_key = 'ux.test.list'");
        await using var s = Scope();

        var forD1 = For<ISavedFilterService>(s.ServiceProvider, Direction);
        (await forD1.SaveAsync(new SaveFilterRequest(null, "ux.test.list", "Privé D1", Json("""{"status":"open"}""")))).IsSuccess.Should().BeTrue();
        (await forD1.SaveAsync(new SaveFilterRequest(null, "ux.test.list", "Partagé D1", Json("""{"status":"paid"}"""), Shared: true))).IsSuccess.Should().BeTrue();

        var forD2 = For<ISavedFilterService>(s.ServiceProvider, Compta);
        var seenByD2 = await forD2.ListAsync("ux.test.list");
        seenByD2.Should().ContainSingle(f => f.Name == "Partagé D1" && !f.Mine);
        seenByD2.Should().NotContain(f => f.Name == "Privé D1");
    }

    // ---- §22 : un seul filtre par défaut par vue ----
    [Fact]
    public async Task Saved_filter_only_one_default_per_view()
    {
        await Sql($"DELETE FROM nexora.saved_filter WHERE view_key = 'ux.def.list'");
        await using var s = Scope();
        var filters = For<ISavedFilterService>(s.ServiceProvider, Direction);
        var first = await filters.SaveAsync(new SaveFilterRequest(null, "ux.def.list", "A", Json("{}"), IsDefault: true));
        await filters.SaveAsync(new SaveFilterRequest(null, "ux.def.list", "B", Json("{}"), IsDefault: true));

        var all = await filters.ListAsync("ux.def.list");
        all.Count(f => f.IsDefault).Should().Be(1);
        all.Single(f => f.IsDefault).Name.Should().Be("B");
        all.Single(f => f.Id == first.Value!.Id).IsDefault.Should().BeFalse();
    }

    // ---- §11, §37-38 : raccourcis — création, conflit de chord, suppression ----
    [Fact]
    public async Task Shortcut_crud_and_chord_conflict()
    {
        await Sql($"DELETE FROM nexora.ui_shortcut WHERE user_id = '{Direction}'");
        await using var s = Scope();
        var shortcuts = For<IShortcutService>(s.ServiceProvider, Direction);

        var made = await shortcuts.SaveAsync(new SaveShortcutRequest(null, "g v", "/pos", "Aller au POS"));
        made.IsSuccess.Should().BeTrue(made.Error?.Message);

        (await shortcuts.SaveAsync(new SaveShortcutRequest(null, "g v", "/orders", "Autre")))
            .Error!.Code.Should().Be("shortcut.chord_taken");

        (await shortcuts.ListAsync()).Should().ContainSingle();
        (await shortcuts.DeleteAsync(made.Value!.Id)).IsSuccess.Should().BeTrue();
        (await shortcuts.ListAsync()).Should().BeEmpty();
    }

    // ---- §50 : la version du design system expose tokens & composants ----
    [Fact]
    public void Design_system_version_lists_tokens_and_components()
    {
        using var s = Scope();
        var ds = s.ServiceProvider.GetRequiredService<IDesignSystemService>().GetVersion();

        ds.WcagTarget.Should().Contain("2.2");
        ds.Themes.Should().Contain("high-contrast");
        ds.Components.Should().Contain(["Button", "CommandPalette", "DataGrid"]);
        ds.TokenGroups.Should().Contain(g => g.Group == "color");
        ds.ComponentCount.Should().Be(ds.Components.Count);
    }

    // ---- §13 : garde-fou sur le nombre de widgets ----
    [Fact]
    public async Task Dashboard_layout_rejects_too_many_widgets()
    {
        await using var s = Scope();
        var layouts = For<IDashboardLayoutService>(s.ServiceProvider, Direction);
        var big = "[" + string.Join(",", Enumerable.Range(0, 50).Select(i => $$"""{"key":"w{{i}}","x":0,"y":0,"w":1,"h":1}""")) + "]";
        (await layouts.SaveAsync(new SaveDashboardLayoutRequest(null, "Trop", Json(big))))
            .Error!.Code.Should().Be("layout.widgets");
    }
}
