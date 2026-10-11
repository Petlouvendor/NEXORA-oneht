using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Intelligence;
using Nexora.Application.Mobile;
using Nexora.Application.Sync;
using Nexora.Contracts.Intelligence;
using Nexora.Contracts.Mobile;
using Nexora.Contracts.Sync;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Mobile, terrain & multi-appareils (Phase 44 §12-13, §20, §22-23, §25-29, §32, §46, §48).
/// Le moteur offline (Phase 10) est réutilisé tel quel pour les opérations terrain — aucune
/// file séparée : ces tests exercent aussi bien la voie en ligne que <c>ISyncService.PushBatchAsync</c>.
/// </summary>
[Collection("finance")]
public sealed class MobileFieldOpsTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

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

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static JsonElement ToJson(object o) => JsonSerializer.SerializeToElement(o);

    // ---- §20, §32 : appareil mobile personnel + jeton push ----
    [Fact]
    public async Task Device_registration_links_mobile_user_and_accepts_push_token()
    {
        await using var s = Scope();
        var devices = For<IDeviceRegistryService>(s.ServiceProvider, Direction);

        var registered = await devices.RegisterAsync(new RegisterDeviceRequest($"phone-{Guid.NewGuid():N}", "Téléphone test", "mobile", null, null));
        registered.IsSuccess.Should().BeTrue(registered.Error?.Message);

        var linkedUser = await Scalar<Guid?>($"SELECT user_id FROM nexora.sync_device WHERE id = '{registered.Value}'");
        linkedUser.Should().Be(Direction, "un appareil mobile est personnel (§32), contrairement à un poste POS partagé");

        var tokenResult = await devices.RegisterPushTokenAsync(registered.Value, new RegisterPushTokenRequest("expo-token-abc", "fcm"));
        tokenResult.IsSuccess.Should().BeTrue(tokenResult.Error?.Message);

        (await devices.RegisterPushTokenAsync(registered.Value, new RegisterPushTokenRequest("x", "carrier-pigeon")))
            .Error!.Code.Should().Be("sync.push_provider");
    }

    // ---- §22-23 : pièces jointes minimales ----
    [Fact]
    public async Task Attachment_upload_and_read_roundtrip_rejects_bad_kind_and_bad_content()
    {
        await using var s = Scope();
        var attachments = For<IAttachmentService>(s.ServiceProvider, Direction);

        var bytes = "signature-fictive"u8.ToArray();
        var base64 = Convert.ToBase64String(bytes);

        var uploaded = await attachments.UploadAsync(new UploadAttachmentRequest("signature", "delivery_stop", Guid.NewGuid(), base64, "image/png"));
        uploaded.IsSuccess.Should().BeTrue(uploaded.Error?.Message);
        uploaded.Value!.SizeBytes.Should().Be(bytes.Length);

        var read = await attachments.GetContentAsync(uploaded.Value.Id);
        read.IsSuccess.Should().BeTrue(read.Error?.Message);
        read.Value.Content.Should().BeEquivalentTo(bytes);
        read.Value.ContentType.Should().Be("image/png");

        (await attachments.UploadAsync(new UploadAttachmentRequest("video", null, null, base64, "video/mp4")))
            .Error!.Code.Should().Be("attachment.kind");
        (await attachments.UploadAsync(new UploadAttachmentRequest("photo", null, null, "not-base64!!", "image/png")))
            .Error!.Code.Should().Be("attachment.content");
    }

    // ---- §12, §52 : tournée — étape sans commande + notification push du livreur ----
    [Fact]
    public async Task Delivery_route_creation_notifies_driver_and_stop_without_order_completes_with_proof()
    {
        await using var s = Scope();
        var devices = For<IDeviceRegistryService>(s.ServiceProvider, Direction);
        var registered = await devices.RegisterAsync(new RegisterDeviceRequest($"phone-{Guid.NewGuid():N}", "Livreur", "mobile", null, null));
        await devices.RegisterPushTokenAsync(registered.Value, new RegisterPushTokenRequest("push-token-1", "fcm"));

        var delivery = For<IDeliveryService>(s.ServiceProvider, Direction);
        var created = await delivery.CreateRouteAsync(new CreateDeliveryRouteRequest(Direction, TestDates.Today, "Moto-1",
            [new CreateDeliveryStopInput(null, null, "12 Rue Test", 18.5392m, -72.3364m, "+509-0000-0000")]));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        created.Value!.Stops.Should().HaveCount(1);

        var queued = await Scalar<long>($"""
            SELECT COUNT(*) FROM nexora.notification_outbox WHERE channel = 'push' AND address = 'push-token-1'
            """);
        queued.Should().BeGreaterThanOrEqualTo(1, "la création de tournée notifie le livreur sur ses appareils (§20)");

        var stop = created.Value.Stops[0];
        var photo = await For<IAttachmentService>(s.ServiceProvider, Direction).UploadAsync(
            new UploadAttachmentRequest("photo", "delivery_stop", stop.Id, Convert.ToBase64String("photo"u8.ToArray()), "image/jpeg"));
        var sig = await For<IAttachmentService>(s.ServiceProvider, Direction).UploadAsync(
            new UploadAttachmentRequest("signature", "delivery_stop", stop.Id, Convert.ToBase64String("sign"u8.ToArray()), "image/png"));

        var completed = await delivery.CompleteStopAsync(stop.Id, new CompleteDeliveryStopRequest(
            Guid.NewGuid(), 18.54m, -72.34m, sig.Value!.Id, photo.Value!.Id, null, null));
        completed.IsSuccess.Should().BeTrue(completed.Error?.Message);
        completed.Value!.Status.Should().Be("delivered");
        completed.Value.SignatureAttachmentId.Should().Be(sig.Value.Id);

        var route = await delivery.GetRouteAsync(created.Value.Id);
        route.Value!.Status.Should().Be("completed", "toutes les étapes sont closes (§52)");
    }

    // ---- §25-29 : la même file offline rejoue une livraison — dédup + conflit ----
    [Fact]
    public async Task Delivery_stop_replays_through_offline_sync_engine_with_dedup_and_conflict_on_reuse()
    {
        await using var s = Scope();
        var delivery = For<IDeliveryService>(s.ServiceProvider, Direction);
        var created = await delivery.CreateRouteAsync(new CreateDeliveryRouteRequest(Direction, TestDates.Today, null,
            [new CreateDeliveryStopInput(null, null, "Adresse offline", null, null, null)]));
        var stopId = created.Value!.Stops[0].Id;

        var devices = For<IDeviceRegistryService>(s.ServiceProvider, Direction);
        var device = await devices.RegisterAsync(new RegisterDeviceRequest($"dev-{Guid.NewGuid():N}", "Terrain", "mobile", null, null));

        var opUuid = Guid.NewGuid();
        var proof = await Proof(s.ServiceProvider, "signature", stopId);   // phase 12 : preuve exigée
        var payload = ToJson(new { StopId = stopId, Latitude = 18.5m, Longitude = -72.3m, SignatureAttachmentId = (Guid?)proof, PhotoAttachmentId = (Guid?)null, AmountCollected = (decimal?)null, PaymentFinancialAccountId = (Guid?)null });

        var sync = For<ISyncService>(s.ServiceProvider, Direction);
        var pushed = await sync.PushBatchAsync(new PushBatchRequest(device.Value, [new SyncOperationInput(opUuid, "delivery_stop", "update", payload)]));
        pushed.IsSuccess.Should().BeTrue(pushed.Error?.Message);
        pushed.Value!.Operations[0].Status.Should().Be("SYNCED");

        // §13 : rejouer la même opération (même operation_uuid) ne la traite pas deux fois.
        var replayed = await sync.PushBatchAsync(new PushBatchRequest(device.Value, [new SyncOperationInput(opUuid, "delivery_stop", "update", payload)]));
        replayed.Value!.Operations[0].Status.Should().Be("SYNCED");

        // Une NOUVELLE opération sur la même étape déjà livrée devient un conflit métier (§29).
        var secondPayload = ToJson(new { StopId = stopId, Latitude = (decimal?)null, Longitude = (decimal?)null, SignatureAttachmentId = (Guid?)proof, PhotoAttachmentId = (Guid?)null, AmountCollected = (decimal?)null, PaymentFinancialAccountId = (Guid?)null });
        var again = await sync.PushBatchAsync(new PushBatchRequest(device.Value, [new SyncOperationInput(Guid.NewGuid(), "delivery_stop", "update", secondPayload)]));
        again.Value!.Operations[0].Status.Should().Be("CONFLICT");

        var center = await sync.SyncCenterAsync();
        center.Conflicts.Should().Contain(c => c.ConflictType == "business" && c.EntityType == "delivery_stop");
    }

    // ---- §13 : cycle de vie intervention + rejeu hors ligne ----
    [Fact]
    public async Task Field_intervention_lifecycle_online_and_via_offline_sync()
    {
        await using var s = Scope();
        var interventions = For<IFieldInterventionService>(s.ServiceProvider, Direction);

        var created = await interventions.CreateAsync(new CreateInterventionRequest(Direction, null, "Climatiseur bureau", null));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        (await interventions.StartAsync(created.Value!.Id)).Value!.Status.Should().Be("in_progress");

        var completed = await interventions.CompleteAsync(created.Value.Id, new CompleteInterventionRequest(
            Guid.NewGuid(), [new ChecklistItemInput("Filtre nettoyé", true)], [new PartUsedInput(Guid.NewGuid(), 1m)], 45, "RAS", null));
        completed.IsSuccess.Should().BeTrue(completed.Error?.Message);
        completed.Value!.Status.Should().Be("completed");
        completed.Value.TimeSpentMinutes.Should().Be(45);

        // Une seconde intervention, complétée via le moteur offline (§25-29).
        var second = (await interventions.CreateAsync(new CreateInterventionRequest(Direction, null, "Pompe à eau", null))).Value!;
        var devices = For<IDeviceRegistryService>(s.ServiceProvider, Direction);
        var device = await devices.RegisterAsync(new RegisterDeviceRequest($"dev-{Guid.NewGuid():N}", "Technicien", "mobile", null, null));
        var payload = ToJson(new
        {
            InterventionId = second.Id, Checklist = new[] { new { Label = "Vérification", Done = true } },
            PartsUsed = Array.Empty<object>(), TimeSpentMinutes = 20, ReportText = "Remplacé joint", SignatureAttachmentId = (Guid?)null,
        });
        var sync = For<ISyncService>(s.ServiceProvider, Direction);
        var pushed = await sync.PushBatchAsync(new PushBatchRequest(device.Value, [new SyncOperationInput(Guid.NewGuid(), "field_intervention", "update", payload)]));
        pushed.Value!.Operations[0].Status.Should().Be("SYNCED");

        var final = await interventions.ListAsync("completed");
        final.Should().Contain(i => i.Id == second.Id && i.ReportText == "Remplacé joint");
    }

    // ---- §25, §48 : bootstrap mobile — périmètre strict de l'appelant ----
    [Fact]
    public async Task Mobile_bootstrap_pull_and_config_reflect_caller_scope()
    {
        await using var s = Scope();
        var delivery = For<IDeliveryService>(s.ServiceProvider, Direction);
        await delivery.CreateRouteAsync(new CreateDeliveryRouteRequest(Direction, TestDates.Today, null,
            [new CreateDeliveryStopInput(null, null, "Adresse bootstrap", null, null, null)]));

        var interventions = For<IFieldInterventionService>(s.ServiceProvider, Direction);
        await interventions.CreateAsync(new CreateInterventionRequest(Direction, null, "Équipement bootstrap", null));

        var tasks = For<ITaskService>(s.ServiceProvider, Direction);
        await tasks.CreateAsync(new CreateTaskRequest("Tâche bootstrap", null, null, null, Direction));

        var bootstrap = For<IMobileBootstrapService>(s.ServiceProvider, Direction);
        var pulled = await bootstrap.PullAsync();
        pulled.MyDeliveryStops.Should().Contain(x => x.Address == "Adresse bootstrap");
        pulled.MyInterventions.Should().Contain(x => x.EquipmentDescription == "Équipement bootstrap");
        pulled.MyTasks.Should().Contain(t => t.Title == "Tâche bootstrap");

        var config = await bootstrap.ConfigAsync();
        config.SupportedOfflineEntityTypes.Should().Contain(["sale", "delivery_stop", "field_intervention"]);
    }

    // ---- Finalisation phase 12 : assigné → en route → sur place → livré (preuve) ; règles d'encaissement et de position ----
    private static readonly Guid Cashier = Guid.Parse("dce00000-0000-0000-0000-0000000000d3");

    private async Task<Guid> Proof(IServiceProvider sp, string kind, Guid stopId)
        => (await For<IAttachmentService>(sp, Direction).UploadAsync(new UploadAttachmentRequest(kind, "delivery_stop", stopId,
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{kind}-{Guid.NewGuid()}")), kind == "photo" ? "image/jpeg" : "image/png"))).Value!.Id;

    [Fact]
    public async Task Delivery_workflow_requires_proof_and_controls_collection_and_location()
    {
        await using var s = Scope();
        var delivery = For<IDeliveryService>(s.ServiceProvider, Direction);
        var today = TestDates.Today;

        (await delivery.CreateRouteAsync(new CreateDeliveryRouteRequest(Cashier, today, null, [new CreateDeliveryStopInput(null, null, "x", null, null, null)])))
            .Error!.Code.Should().Be("delivery_route.driver", "le caissier n'a pas accès au terrain");
        (await delivery.CreateRouteAsync(new CreateDeliveryRouteRequest(Direction, today, null, [new CreateDeliveryStopInput(null, null, "x", 95m, 0m, null)])))
            .Error!.Code.Should().Be("delivery_route.coordinates");
        (await delivery.CreateRouteAsync(new CreateDeliveryRouteRequest(Direction, today.AddDays(-3), null, [new CreateDeliveryStopInput(null, null, "x", null, null, null)])))
            .Error!.Code.Should().Be("delivery_route.date");

        var route = (await delivery.CreateRouteAsync(new CreateDeliveryRouteRequest(Direction, today, "Moto-2", [
            new CreateDeliveryStopInput(null, null, "Rue A", null, null, null),
            new CreateDeliveryStopInput(null, null, "Rue B", null, null, null)]))).Value!;
        route.Status.Should().Be("planned");
        route.DriverName.Should().NotBeNull();
        var (a, b) = (route.Stops[0], route.Stops[1]);

        // En route : la tournée démarre ; sur place ; impossible de revenir « en route ».
        (await delivery.AdvanceStopAsync(a.Id, "en_route")).Value!.EnRouteAt.Should().NotBeNull();
        (await delivery.GetRouteAsync(route.Id)).Value!.Status.Should().Be("in_progress", "la tournée restait « planifiée » pendant les livraisons");
        (await delivery.AdvanceStopAsync(a.Id, "arrived")).Value!.ArrivedAt.Should().NotBeNull();
        (await delivery.AdvanceStopAsync(a.Id, "en_route")).Error!.Code.Should().Be("delivery_stop.order");

        // Livré : preuve exigée, du bon type ; pas d'encaissement sans commande.
        (await delivery.CompleteStopAsync(a.Id, new CompleteDeliveryStopRequest(Guid.NewGuid(), null, null, null, null, null, null)))
            .Error!.Code.Should().Be("delivery_stop.proof");
        var photo = await Proof(s.ServiceProvider, "photo", a.Id);
        (await delivery.CompleteStopAsync(a.Id, new CompleteDeliveryStopRequest(Guid.NewGuid(), null, null, photo, null, null, null)))
            .Error!.Code.Should().Be("delivery_stop.attachment", "une photo n'est pas une signature");
        (await delivery.CompleteStopAsync(a.Id, new CompleteDeliveryStopRequest(Guid.NewGuid(), null, null, null, photo, 500m, null)))
            .Error!.Code.Should().Be("delivery_stop.amount", "un encaissement sans commande n'était porté par aucune écriture");

        // Position : non prévue par l'entreprise → jamais enregistrée.
        var done = await delivery.CompleteStopAsync(a.Id, new CompleteDeliveryStopRequest(Guid.NewGuid(), 18.5m, -72.3m, null, photo, null, null));
        done.IsSuccess.Should().BeTrue(done.Error?.Message);
        done.Value!.Status.Should().Be("delivered");
        done.Value.Latitude.Should().BeNull("la géolocalisation n'est pas prévue par l'entreprise");
        done.Value.CompletedByName.Should().NotBeNull();

        // Prévue : position enregistrée et contrôlée.
        await Sql($"""
            INSERT INTO nexora.setting (key, scope, scope_id, company_id, value, note)
            VALUES ('field_ops.capture_location', 'company', '{Company}', '{Company}', 'true', 'test')
            ON CONFLICT DO NOTHING
            """);
        var sig = await Proof(s.ServiceProvider, "signature", b.Id);
        (await delivery.CompleteStopAsync(b.Id, new CompleteDeliveryStopRequest(Guid.NewGuid(), 120m, 0m, sig, null, null, null)))
            .Error!.Code.Should().Be("delivery_stop.coordinates");
        var located = await delivery.CompleteStopAsync(b.Id, new CompleteDeliveryStopRequest(Guid.NewGuid(), 18.54m, -72.33m, sig, null, null, null));
        located.Value!.Latitude.Should().Be(18.54m);
        (await delivery.GetRouteAsync(route.Id)).Value!.Status.Should().Be("completed");
        (await delivery.OptionsAsync()).CaptureLocation.Should().BeTrue();
        await Sql($"DELETE FROM nexora.setting WHERE key = 'field_ops.capture_location' AND company_id = '{Company}'");
    }

    [Fact]
    public async Task Offline_field_operations_are_checked_per_operation_and_interventions_require_a_report()
    {
        await using var s = Scope();
        var delivery = For<IDeliveryService>(s.ServiceProvider, Direction);
        var route = (await delivery.CreateRouteAsync(new CreateDeliveryRouteRequest(Direction, TestDates.Today, null,
            [new CreateDeliveryStopInput(null, null, "Rue C", null, null, null)]))).Value!;
        var stopId = route.Stops[0].Id;
        var sig = await Proof(s.ServiceProvider, "signature", stopId);
        var device = await For<IDeviceRegistryService>(s.ServiceProvider, Direction)
            .RegisterAsync(new RegisterDeviceRequest($"dev-{Guid.NewGuid():N}", "Terrain", "mobile", null, null));
        var payload = ToJson(new { StopId = stopId, Latitude = (decimal?)null, Longitude = (decimal?)null, SignatureAttachmentId = sig,
            PhotoAttachmentId = (Guid?)null, AmountCollected = (decimal?)null, PaymentFinancialAccountId = (Guid?)null });

        // Sans droit terrain (le caissier n'a aucun rôle dans cette entreprise) : l'opération est refusée.
        await using (var s2 = Scope())
        {
            var asCashier = For<ISyncService>(s2.ServiceProvider, Cashier);
            var refused = await asCashier.PushBatchAsync(new PushBatchRequest(device.Value, [new SyncOperationInput(Guid.NewGuid(), "delivery_stop", "update", payload)]));
            refused.Value!.Operations[0].Status.Should().Be("FAILED");
            refused.Value.Operations[0].Message.Should().Contain("field_ops.manage");
        }
        var ok = await For<ISyncService>(s.ServiceProvider, Direction)
            .PushBatchAsync(new PushBatchRequest(device.Value, [new SyncOperationInput(Guid.NewGuid(), "delivery_stop", "update", payload)]));
        ok.Value!.Operations[0].Status.Should().Be("SYNCED");

        var interventions = For<IFieldInterventionService>(s.ServiceProvider, Direction);
        (await interventions.CreateAsync(new CreateInterventionRequest(Cashier, null, "Climatiseur", null))).Error!.Code.Should().Be("field_intervention.technician");
        var i = (await interventions.CreateAsync(new CreateInterventionRequest(Direction, null, "Groupe électrogène", null))).Value!;
        (await interventions.CompleteAsync(i.Id, new CompleteInterventionRequest(Guid.NewGuid(), [], [], 30, "", null)))
            .Error!.Code.Should().Be("field_intervention.report");
        var done = await interventions.CompleteAsync(i.Id, new CompleteInterventionRequest(Guid.NewGuid(), [new ChecklistItemInput("Vidange", true)], [], 30, "Vidange faite", null));
        done.Value!.Status.Should().Be("completed");
        done.Value.StartedAt.Should().NotBeNull("hors ligne, le début se déduit du temps passé");
        done.Value.TechnicianName.Should().NotBeNull();
    }
}
