using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Migration;
using Nexora.Contracts.Migration;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Migration, importation &amp; onboarding avancé (Phase 39 §47) : CSV, profilage, mapping,
/// validation, simulation sans impact (§23), idempotence &amp; modes (§28-29), rollback (§37),
/// rapprochement (§35), checklist d'onboarding (§32-34).
/// </summary>
[Collection("finance")]
public sealed class MigrationImportTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid User = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private (IImportService Imports, IOnboardingService Onboarding) Svcs(IServiceProvider sp)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(User);
        tc.SetScope([Company], [], Company);
        return (sp.GetRequiredService<IImportService>(), sp.GetRequiredService<IOnboardingService>());
    }

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

    private static IReadOnlyList<ColumnMappingDto> Map(params (string t, string s, string? tr)[] pairs)
        => pairs.Select(p => new ColumnMappingDto(p.t, p.s, null, p.tr)).ToList();

    /// <summary>Enchaîne create → profile → mapping → validate → simulate → execute.</summary>
    private async Task<ImportJobDto> RunAsync(IImportService imports, string kind, string csv,
        IReadOnlyList<ColumnMappingDto> mapping, string mode = "upsert")
    {
        var created = await imports.CreateAsync(new CreateImportRequest(kind, "csv", csv, "t.csv", mode));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        var id = created.Value!.Id;
        (await imports.ProfileAsync(id)).IsSuccess.Should().BeTrue();
        (await imports.SetMappingAsync(id, new SetMappingRequest(mapping, null))).IsSuccess.Should().BeTrue();
        (await imports.ValidateAsync(id)).IsSuccess.Should().BeTrue();
        (await imports.SimulateAsync(id)).IsSuccess.Should().BeTrue();
        var done = await imports.ExecuteAsync(id);
        done.IsSuccess.Should().BeTrue(done.Error?.Message);
        return done.Value!;
    }

    // ---- §6 : le CSV est parsé, profilé, les types détectés ----
    [Fact]
    public async Task Csv_is_parsed_profiled_and_types_detected()
    {
        await using var s = Scope();
        var (imports, _) = Svcs(s.ServiceProvider);
        var csv = "code;nom;email;limite\nA1;Alpha;a@x.io;1000,50\nA2;Beta;b@x.io;2000";
        var c = await imports.CreateAsync(new CreateImportRequest("customers", "csv", csv, "c.csv"));
        var prof = await imports.ProfileAsync(c.Value!.Id);

        prof.Value!.Profile!.RowCount.Should().Be(2);
        prof.Value.Profile.Columns.Should().BeEquivalentTo(["code", "nom", "email", "limite"]);
        prof.Value.Profile.ColumnProfiles.Single(p => p.Name == "email").DetectedType.Should().Be("email");
        prof.Value.Profile.ColumnProfiles.Single(p => p.Name == "limite").DetectedType.Should().Be("decimal");
        prof.Value.Profile.ColumnProfiles.Single(p => p.Name == "code").DetectedType.Should().Be("text");
    }

    // ---- §7, §33 : le mapping automatique relie les colonnes aux champs cibles ----
    [Fact]
    public async Task Suggest_mapping_matches_columns_to_target_fields()
    {
        await using var s = Scope();
        var (imports, _) = Svcs(s.ServiceProvider);
        var sug = await imports.SuggestMappingAsync("customers", ["Code Client", "Raison sociale", "Téléphone", "E-mail", "Plafond"]);
        sug.Should().Contain(x => x.TargetField == "code" && x.SourceColumn == "Code Client");
        sug.Should().Contain(x => x.TargetField == "name" && x.SourceColumn == "Raison sociale");
        sug.Should().Contain(x => x.TargetField == "phone" && x.SourceColumn == "Téléphone");
        sug.Should().Contain(x => x.TargetField == "credit_limit" && x.SourceColumn == "Plafond");
    }

    // ---- §25 : la validation signale les champs obligatoires manquants ----
    [Fact]
    public async Task Validate_flags_missing_required_fields()
    {
        await using var s = Scope();
        var (imports, _) = Svcs(s.ServiceProvider);
        var csv = "code,name\nMIG-V1,\n,Sans Code";
        var c = await imports.CreateAsync(new CreateImportRequest("customers", "csv", csv, "v.csv"));
        await imports.ProfileAsync(c.Value!.Id);
        await imports.SetMappingAsync(c.Value.Id, new SetMappingRequest(Map(("code", "code", "trim"), ("name", "name", "trim")), null));
        var v = await imports.ValidateAsync(c.Value.Id);

        v.Value!.ErrorRows.Should().Be(2);
        var rows = await imports.ListRowsAsync(c.Value.Id, "error");
        rows.Should().HaveCount(2);
        rows.SelectMany(r => r.Messages).Should().Contain(msg => msg.Message.Contains("obligatoire"));
    }

    // ---- §23 : la simulation ne modifie rien en production ----
    [Fact]
    public async Task Simulation_makes_no_production_changes()
    {
        await using var s = Scope();
        var (imports, _) = Svcs(s.ServiceProvider);
        var csv = "code,name\nMIG-SIM-1,Client Simulé\nMIG-SIM-2,Autre Simulé";
        var c = await imports.CreateAsync(new CreateImportRequest("customers", "csv", csv, "sim.csv"));
        await imports.ProfileAsync(c.Value!.Id);
        await imports.SetMappingAsync(c.Value.Id, new SetMappingRequest(Map(("code", "code", "trim"), ("name", "name", "trim")), null));
        await imports.ValidateAsync(c.Value.Id);
        var sim = await imports.SimulateAsync(c.Value.Id);

        sim.Value!.Simulation!.WouldCreate.Should().Be(2);
        (await Scalar<long>("SELECT COUNT(*) FROM nexora.customer WHERE customer_number LIKE 'MIG-SIM-%'")).Should().Be(0);
    }

    // ---- §11, §28 : import clients → création + idempotence ----
    [Fact]
    public async Task Customer_import_creates_records_and_is_idempotent()
    {
        await Sql("DELETE FROM nexora.customer WHERE customer_number LIKE 'MIG-CUST-%'");
        await using var s = Scope();
        var (imports, _) = Svcs(s.ServiceProvider);
        var csv = "code,name,phone\nMIG-CUST-1,Import Un,509-1000\nMIG-CUST-2,Import Deux,509-2000";
        var map = Map(("code", "code", "trim"), ("name", "name", "trim"), ("phone", "phone", "phone"));

        var first = await RunAsync(imports, "customers", csv, map);
        first.CreatedCount.Should().Be(2);
        (await Scalar<long>("SELECT COUNT(*) FROM nexora.customer WHERE customer_number LIKE 'MIG-CUST-%'")).Should().Be(2);

        var second = await RunAsync(imports, "customers", csv, map, "skip_duplicates");
        second.CreatedCount.Should().Be(0);
        second.SkippedCount.Should().Be(2);
        (await Scalar<long>("SELECT COUNT(*) FROM nexora.customer WHERE customer_number LIKE 'MIG-CUST-%'")).Should().Be(2);
    }

    // ---- §29 : le mode « rejeter les doublons » rejette la 2ᵉ occurrence ----
    [Fact]
    public async Task Reject_duplicates_mode_rejects_the_repeated_row()
    {
        await Sql("DELETE FROM nexora.customer WHERE customer_number = 'MIG-DUP-1'");
        await using var s = Scope();
        var (imports, _) = Svcs(s.ServiceProvider);
        var csv = "code,name\nMIG-DUP-1,Premier\nMIG-DUP-1,Doublon";
        var job = await RunAsync(imports, "customers", csv, Map(("code", "code", "trim"), ("name", "name", "trim")), "reject_duplicates");

        job.CreatedCount.Should().Be(1);
        job.RejectedCount.Should().Be(1);
        var rejected = await imports.ListRowsAsync(job.Id, "rejected");
        rejected.Should().ContainSingle();
    }

    // ---- §11 : import produits → catégorie & unité créées à la volée ----
    [Fact]
    public async Task Product_import_autocreates_category_and_unit()
    {
        await Sql("DELETE FROM nexora.product_price WHERE product_id IN (SELECT id FROM nexora.product WHERE sku LIKE 'MIG-P-%')");
        await Sql("DELETE FROM nexora.product WHERE sku LIKE 'MIG-P-%'");
        await Sql("DELETE FROM nexora.product_category WHERE name = 'Migration Test Cat'");
        await using var s = Scope();
        var (imports, _) = Svcs(s.ServiceProvider);
        var csv = "sku,name,category,unit,price\nMIG-P-1,Article Un,Migration Test Cat,BOX,12.5\nMIG-P-2,Article Deux,Migration Test Cat,BOX,7";
        var map = Map(("sku", "sku", "trim"), ("name", "name", "trim"), ("category", "category", "trim"),
            ("unit", "unit", "trim"), ("sale_price", "price", "decimal"));

        var job = await RunAsync(imports, "products", csv, map);

        job.CreatedCount.Should().Be(2);
        (await Scalar<long>("SELECT COUNT(*) FROM nexora.product_category WHERE company_id = '" + Company + "' AND name = 'Migration Test Cat'")).Should().Be(1);
        (await Scalar<long>("SELECT COUNT(*) FROM nexora.unit WHERE company_id = '" + Company + "' AND upper(code) = 'BOX'")).Should().Be(1);
        (await Scalar<decimal?>("SELECT amount FROM nexora.product_price pp JOIN nexora.product p ON p.id = pp.product_id WHERE p.sku = 'MIG-P-1'")).Should().Be(12.5m);
    }

    // ---- §14 : import du stock d'ouverture → mouvement « opening » ----
    [Fact]
    public async Task Opening_stock_posts_an_opening_movement()
    {
        await Sql("DELETE FROM nexora.stock_movement WHERE reference_type = 'import' AND product_id IN (SELECT id FROM nexora.product WHERE sku = 'MIG-OS-1')");
        await Sql("DELETE FROM nexora.product WHERE sku = 'MIG-OS-1'");
        await using var s = Scope();
        var (imports, _) = Svcs(s.ServiceProvider);

        // Produit d'abord.
        await RunAsync(imports, "products", "sku,name\nMIG-OS-1,Produit Stock",
            Map(("sku", "sku", "trim"), ("name", "name", "trim")));

        // Emplacement de démo de la Pharmacie (code « C1 » sur le comptoir → inventory_location a003).
        var loc = await Scalar<string>("SELECT id::text FROM nexora.inventory_location WHERE company_id = '" + Company + "' LIMIT 1");
        var locCode = await Scalar<string>("SELECT COALESCE(code, name) FROM nexora.inventory_location WHERE id = '" + loc + "'");

        var csv = $"sku,loc,qty,cost\nMIG-OS-1,{locCode},40,3.25";
        var job = await RunAsync(imports, "opening_stock", csv,
            Map(("sku", "sku", "trim"), ("location", "loc", "trim"), ("quantity", "qty", "decimal"), ("cost", "cost", "decimal")));

        job.CreatedCount.Should().Be(1);
        (await Scalar<decimal?>("SELECT quantity FROM nexora.stock_movement WHERE reference_type = 'import' AND movement_type = 'opening' AND product_id = (SELECT id FROM nexora.product WHERE sku = 'MIG-OS-1')"))
            .Should().Be(40m);
    }

    // ---- §37 : l'annulation défait les entités créées ----
    [Fact]
    public async Task Cancel_rolls_back_created_entities()
    {
        await Sql("DELETE FROM nexora.customer WHERE customer_number LIKE 'MIG-RB-%'");
        await using var s = Scope();
        var (imports, _) = Svcs(s.ServiceProvider);
        var job = await RunAsync(imports, "customers", "code,name\nMIG-RB-1,Rollback Un\nMIG-RB-2,Rollback Deux",
            Map(("code", "code", "trim"), ("name", "name", "trim")));
        (await Scalar<long>("SELECT COUNT(*) FROM nexora.customer WHERE customer_number LIKE 'MIG-RB-%'")).Should().Be(2);

        var canceled = await imports.CancelAsync(job.Id);
        canceled.Value!.Status.Should().Be("canceled");
        (await Scalar<long>("SELECT COUNT(*) FROM nexora.customer WHERE customer_number LIKE 'MIG-RB-%'")).Should().Be(0);
    }

    // ---- §35 : le rapprochement compare source et NEXORA ONE ----
    [Fact]
    public async Task Reconciliation_compares_source_and_nexora()
    {
        await Sql("DELETE FROM nexora.customer WHERE customer_number LIKE 'MIG-REC-%'");
        await using var s = Scope();
        var (imports, _) = Svcs(s.ServiceProvider);
        var job = await RunAsync(imports, "customers", "code,name\nMIG-REC-1,Rec Un\nMIG-REC-2,Rec Deux\nMIG-REC-3,Rec Trois",
            Map(("code", "code", "trim"), ("name", "name", "trim")));

        var recon = await imports.ReconcileAsync(job.Id);
        recon.Value!.AllReconciled.Should().BeTrue();
        recon.Value.Lines.Should().Contain(l => l.Metric.Contains("source") && l.SourceValue == 3);
        (await imports.ListEventsAsync(job.Id)).Select(e => e.EventType).Should().Contain(MigrationEvents.MigrationReconciled);
    }

    // ---- §32-34 : la checklist d'onboarding dérive les items et le score ----
    [Fact]
    public async Task Onboarding_checklist_derives_items_and_score()
    {
        await using var s = Scope();
        var (_, onboarding) = Svcs(s.ServiceProvider);
        var cl = await onboarding.GetChecklistAsync();

        cl.Items.Should().Contain(i => i.Key == "company" && i.Status == "done");
        cl.Items.Should().Contain(i => i.Key == "customers");   // la Pharmacie a un client de démo
        cl.Items.Single(i => i.Key == "notifications").Source.Should().Be("manual");
        cl.Total.Should().Be(cl.Items.Count);
        cl.Score.Quality.Should().BeInRange(0, 100);

        var completed = await onboarding.CompleteTaskAsync(new CompleteOnboardingTaskRequest("tests", "done", "OK en recette"));
        completed.Value!.Items.Single(i => i.Key == "tests").Status.Should().Be("done");
    }
}
