using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Configuration;
using Nexora.Contracts.Configuration;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>Configuration & adaptabilité — scénarios de validation Phase 32 §56.</summary>
[Collection("finance")]
public sealed class ConfigAdaptabilityTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Resto = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid User = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private async Task Sql(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    // ---- §6, §9, §11-12 : appliquer un profil active modules + crée champs + terminologie ----
    [Fact]
    public async Task Apply_pharmacy_profile_activates_modules_and_creates_fields()
    {
        await using var s = Scope();
        var profiles = s.ServiceProvider.GetRequiredService<ISectorProfileService>();

        var res = await profiles.ApplyAsync("pharmacy", new ApplyProfileRequest(DryRun: false));
        res.IsSuccess.Should().BeTrue(res.Error?.Message);

        // état final (assertions indépendantes de l'ordre des tests / d'une application antérieure)
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.company_module WHERE company_id='{Company}' AND module_key='pharmacy' AND enabled"))
            .Should().Be(1);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.custom_field WHERE company_id='{Company}' AND entity_type='customer' AND field_key='insurance_number'"))
            .Should().Be(1);
        (await Scalar<string>($"SELECT label FROM nexora.terminology_override WHERE company_id='{Company}' AND term_key='customer' AND locale='fr'"))
            .Should().Be("Patient");
        (await Scalar<string>($"SELECT value::text FROM nexora.setting WHERE company_id='{Company}' AND key='stock.expiry_alert_days'"))
            .Should().Be("60");

        // ré-application idempotente : rien de neuf
        var again = await profiles.ApplyAsync("pharmacy", new ApplyProfileRequest());
        again.Value!.FieldsCreated.Should().Be(0);
    }

    // ---- §44, §53 : l'application est versionnée et journalisée ----
    [Fact]
    public async Task Profile_apply_is_versioned_and_logged()
    {
        await using var s = Scope();
        var profiles = s.ServiceProvider.GetRequiredService<ISectorProfileService>();

        var res = await profiles.ApplyAsync("wholesale", new ApplyProfileRequest());
        res.IsSuccess.Should().BeTrue(res.Error?.Message);
        res.Value!.ConfigVersionId.Should().NotBeNull();

        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.config_version WHERE company_id='{Company}' AND domain='sector_profile'"))
            .Should().BeGreaterThanOrEqualTo(1);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.config_transfer WHERE company_id='{Company}' AND kind='apply_profile' AND source_profile_key='wholesale'"))
            .Should().BeGreaterThanOrEqualTo(1);
    }

    // ---- §50-51 : la prévisualisation ne change rien ----
    [Fact]
    public async Task Preview_reports_items_without_changing_anything()
    {
        await using var s = Scope();
        var profiles = s.ServiceProvider.GetRequiredService<ISectorProfileService>();

        var before = await Scalar<long>($"SELECT COUNT(*) FROM nexora.custom_field WHERE company_id='{Company}' AND field_key='service_period'");
        var preview = await profiles.PreviewAsync("restaurant");
        preview.IsSuccess.Should().BeTrue(preview.Error?.Message);
        preview.Value!.Items.Should().Contain(i => i.Kind == "module" && i.Key == "restaurant");
        var after = await Scalar<long>($"SELECT COUNT(*) FROM nexora.custom_field WHERE company_id='{Company}' AND field_key='service_period'");
        after.Should().Be(before);
    }

    // ---- §47-48 : l'export exclut les réglages sensibles et l'historique ----
    [Fact]
    public async Task Export_bundle_excludes_sensitive_settings()
    {
        await using var s = Scope();
        var transfer = s.ServiceProvider.GetRequiredService<IConfigTransferService>();
        var profiles = s.ServiceProvider.GetRequiredService<ISectorProfileService>();
        await profiles.ApplyAsync("pharmacy", new ApplyProfileRequest());

        var bundle = await transfer.ExportAsync();
        bundle.SchemaVersion.Should().Be("1.0");
        bundle.SourceCompanyId.Should().Be(Company);
        bundle.Modules.Should().Contain(m => m.Key == "pharmacy");
        bundle.CustomFields.Should().Contain(f => f.FieldKey == "insurance_number");

        foreach (var set in bundle.Settings)
        {
            var sensitive = await Scalar<bool>($"SELECT is_sensitive FROM nexora.setting_definition WHERE key='{set.Key}'");
            sensitive.Should().BeFalse($"{set.Key} ne doit pas être exporté");
        }
    }

    // ---- §48 : clonage vers une autre entreprise autorisée, sans transactions ----
    [Fact]
    public async Task Clone_copies_config_to_another_authorized_company()
    {
        await using var s = Scope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<Nexora.Application.Abstractions.ITenantContext>();
        tc.SetUser(User);
        tc.SetScope([Company, Resto], [], Company);

        var profiles = s.ServiceProvider.GetRequiredService<ISectorProfileService>();
        var transfer = s.ServiceProvider.GetRequiredService<IConfigTransferService>();
        await profiles.ApplyAsync("pharmacy", new ApplyProfileRequest());

        var restoSalesBefore = await Scalar<long>($"SELECT COUNT(*) FROM nexora.sale WHERE company_id='{Resto}'");

        var clone = await transfer.CloneAsync(new CloneConfigRequest(Resto));
        clone.IsSuccess.Should().BeTrue(clone.Error?.Message);
        clone.Value!.ModulesApplied.Should().BeGreaterThanOrEqualTo(1);

        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.company_module WHERE company_id='{Resto}' AND module_key='pharmacy'"))
            .Should().Be(1);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.sale WHERE company_id='{Resto}'"))
            .Should().Be(restoSalesBefore);   // aucune donnée transactionnelle clonée (§48)
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.config_transfer WHERE company_id='{Resto}' AND kind='clone'"))
            .Should().BeGreaterThanOrEqualTo(1);

        (await transfer.CloneAsync(new CloneConfigRequest(Company))).Error!.Code.Should().Be("config.clone_self");
    }

    // ---- §52 : garde-fou — mapping comptable essentiel manquant bloque la publication ----
    [Fact]
    public async Task Guardrails_block_on_a_missing_core_mapping()
    {
        await Sql($"DELETE FROM nexora.account_mapping WHERE company_id='{Company}' AND mapping_key='inventory'");
        try
        {
            await using var s = Scope();
            var gr = s.ServiceProvider.GetRequiredService<IConfigGuardrails>();
            var report = await gr.ValidateAsync();
            report.Checks.Should().Contain(c => c.Key == "required_mappings" && c.Status == "fail");
            report.CanPublish.Should().BeFalse();

            var profiles = s.ServiceProvider.GetRequiredService<ISectorProfileService>();
            (await profiles.ApplyAsync("retail", new ApplyProfileRequest())).Error!.Code.Should().Be("config.guardrails_failed");
        }
        finally
        {
            await Sql($"""
                INSERT INTO nexora.account_mapping (company_id, mapping_key, account_id)
                SELECT '{Company}', 'inventory', a.id FROM nexora.account a
                WHERE a.company_id='{Company}' AND a.code='3110' ON CONFLICT DO NOTHING
                """);
        }
    }

    // ---- §52 : les garde-fous passent sur l'entreprise de démo ----
    [Fact]
    public async Task Guardrails_pass_on_the_demo_company()
    {
        await using var s = Scope();
        var gr = s.ServiceProvider.GetRequiredService<IConfigGuardrails>();
        var report = await gr.ValidateAsync();

        report.Checks.Should().Contain(c => c.Key == "currency_valid" && c.Status == "pass");
        report.Checks.Should().Contain(c => c.Key == "required_mappings" && c.Status == "pass");
        report.CanPublish.Should().BeTrue();
    }
}
