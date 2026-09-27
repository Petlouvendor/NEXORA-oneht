using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Notifications;
using Nexora.Contracts.Notifications;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>Moteur de notifications — déduplication, préférences, gabarits, file de remise (Phase 34 §47).</summary>
[Collection("finance")]
public sealed class NotificationEngineTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");

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

    private async Task<Guid> NewUser()
    {
        var id = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{id}', 'notif-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), 'Notif test', 'active')
            """);
        return id;
    }

    private static void AsUser(IServiceProvider sp, Guid userId)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(userId);
        tc.SetScope([Company], [], Company);
    }

    private static Dictionary<string, string?> Vars(params (string, string?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    // ---- §2, §4 : une émission crée la notification interne ----
    [Fact]
    public async Task Notify_creates_an_in_app_notification()
    {
        await using var s = Scope();
        var uid = await NewUser();
        AsUser(s.ServiceProvider, uid);
        var svc = s.ServiceProvider.GetRequiredService<INotificationService>();

        var res = await svc.NotifyAsync(new NotifyRequest(
            "low_stock", [uid], "stock.low",
            Vars(("product.name", "Paracétamol"), ("stock.on_hand", "3"), ("stock.reorder_point", "10")),
            Priority: 3, Source: "automation"));

        res.NotificationsCreated.Should().Be(1);
        var page = await svc.ListAsync(new NotificationQuery("unread"));
        page.Items.Should().ContainSingle(n => n.Category == "low_stock" && n.Title.Contains("Paracétamol") && n.Message.Contains("3 en stock"));
        (await svc.UnreadCountAsync()).Should().Be(1);
    }

    // ---- §5 : un canal désactivé n'est pas mis en file ; un canal activé l'est ----
    [Fact]
    public async Task Channel_preferences_decide_which_outbox_rows_are_queued()
    {
        await using var s = Scope();
        var uid = await NewUser();
        AsUser(s.ServiceProvider, uid);
        var prefs = s.ServiceProvider.GetRequiredService<INotificationPreferenceService>();
        var svc = s.ServiceProvider.GetRequiredService<INotificationService>();

        // par défaut : in-app seulement → aucune ligne d'outbox
        var d1 = await svc.NotifyAsync(new NotifyRequest("dunning", [uid], "dunning.reminder",
            Vars(("level", "1"), ("customer.name", "ACME"), ("reminder.title", "Rappel"),
                 ("invoice.balance", "100"), ("invoice.days_late", "3")), DedupKey: "t-default"));
        d1.OutboxQueued.Should().Be(0);

        // email activé → une ligne d'outbox email
        (await prefs.UpsertAsync(new UpsertNotificationPreferenceRequest(Company, "dunning", InApp: true, Email: true))).IsSuccess.Should().BeTrue();
        var d2 = await svc.NotifyAsync(new NotifyRequest("dunning", [uid], "dunning.reminder",
            Vars(("level", "2"), ("customer.name", "ACME"), ("reminder.title", "Relance"),
                 ("invoice.balance", "200"), ("invoice.days_late", "10")), DedupKey: "t-email"));
        d2.OutboxQueued.Should().Be(1);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.notification_outbox WHERE user_id='{uid}' AND channel='email'")).Should().Be(1);
    }

    // ---- §5 : sous la priorité minimale → rien ----
    [Fact]
    public async Task Below_the_minimum_priority_the_notification_is_dropped()
    {
        await using var s = Scope();
        var uid = await NewUser();
        AsUser(s.ServiceProvider, uid);
        var prefs = s.ServiceProvider.GetRequiredService<INotificationPreferenceService>();
        var svc = s.ServiceProvider.GetRequiredService<INotificationService>();

        await prefs.UpsertAsync(new UpsertNotificationPreferenceRequest(Company, null, MinPriority: 2));

        var res = await svc.NotifyAsync(new NotifyRequest("low_stock", [uid], "stock.low",
            Vars(("product.name", "X"), ("stock.on_hand", "1"), ("stock.reorder_point", "5")), Priority: 4));

        res.NotificationsCreated.Should().Be(0);
        res.Skipped.Should().Be(1);
        (await svc.UnreadCountAsync()).Should().Be(0);
    }

    // ---- §30 : la clé de déduplication empêche un doublon ----
    [Fact]
    public async Task The_dedup_key_prevents_a_duplicate_notification()
    {
        await using var s = Scope();
        var uid = await NewUser();
        AsUser(s.ServiceProvider, uid);
        var svc = s.ServiceProvider.GetRequiredService<INotificationService>();

        var req = new NotifyRequest("expiring_lot", [uid], "stock.expiring",
            Vars(("lot.number", "L-42"), ("lot.expiration_date", "2026-09-30")),
            DedupKey: "expiring_lot:L-42");

        (await svc.NotifyAsync(req)).NotificationsCreated.Should().Be(1);
        var second = await svc.NotifyAsync(req);
        second.NotificationsCreated.Should().Be(0);
        second.Skipped.Should().Be(1);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.notification WHERE user_id='{uid}' AND dedup_key='expiring_lot:L-42'")).Should().Be(1);
    }

    // ---- §23 : le rendu ne substitue que les variables fournies, ignore l'inconnu, n'injecte rien ----
    [Fact]
    public async Task Template_rendering_is_whitelisted_and_flags_missing_variables()
    {
        await using var s = Scope();
        AsUser(s.ServiceProvider, await NewUser());
        var templates = s.ServiceProvider.GetRequiredService<IMessageTemplateService>();

        var rendered = await templates.RenderAsync(new RenderTemplateRequest("dunning.reminder", "email", "fr",
            Vars(("customer.name", "ACME"), ("company.name", "NEXORA"),
                 ("evil", "{{invoice.balance}}"))));   // valeur injectée : ne doit pas être ré-interprétée

        rendered.IsSuccess.Should().BeTrue(rendered.Error?.Message);
        rendered.Value!.Body.Should().Contain("ACME").And.Contain("NEXORA");
        rendered.Value.Body.Should().NotContain("{{customer.name}}");
        rendered.Value.MissingVariables.Should().Contain("invoice.balance");   // non fournie → signalée
        rendered.Value.Body.Should().NotContain("{{evil}}");                   // jeton inconnu → vide, jamais la valeur
    }

    // ---- §28-31 : le distributeur réessaie puis place en dead-letter ----
    [Fact]
    public async Task The_dispatcher_dead_letters_after_the_maximum_attempts()
    {
        await using var s = Scope();
        var uid = await NewUser();
        AsUser(s.ServiceProvider, uid);
        var dispatcher = s.ServiceProvider.GetRequiredService<INotificationDispatcher>();

        // canal webhook sans adresse → échec réessayable ; max 2 tentatives.
        var outboxId = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.notification_outbox (id, company_id, user_id, channel, category, subject, body, max_attempts, next_attempt_at)
            VALUES ('{outboxId}', '{Company}', '{uid}', 'webhook', 'low_stock', 'x', 'y', 2, now() - interval '1 minute')
            """);

        await dispatcher.DispatchDueAsync();
        (await Scalar<string>($"SELECT status FROM nexora.notification_outbox WHERE id='{outboxId}'")).Should().Be("failed");

        await Sql($"UPDATE nexora.notification_outbox SET next_attempt_at = now() - interval '1 minute' WHERE id='{outboxId}'");
        await dispatcher.DispatchDueAsync();

        (await Scalar<string>($"SELECT status FROM nexora.notification_outbox WHERE id='{outboxId}'")).Should().Be("dead");
        (await Scalar<short>($"SELECT attempts FROM nexora.notification_outbox WHERE id='{outboxId}'")).Should().Be((short)2);
    }

    // ---- §4 : marquer lu met à jour le compteur ; le périmètre par requête reste l'utilisateur ----
    [Fact]
    public async Task Marking_read_and_the_per_user_scope()
    {
        var alice = await NewUser();
        var bob = await NewUser();

        await using var s = Scope();
        var svc = s.ServiceProvider.GetRequiredService<INotificationService>();

        AsUser(s.ServiceProvider, alice);
        await svc.NotifyAsync(new NotifyRequest("system", [alice, bob], "stock.low",
            Vars(("product.name", "P"), ("stock.on_hand", "0"), ("stock.reorder_point", "1")), DedupKey: "shared-1"));

        // Alice ne voit que la sienne (la requête filtre WHERE user_id = courant).
        var aPage = await svc.ListAsync(new NotificationQuery());
        aPage.Items.Should().HaveCount(1);
        var id = aPage.Items[0].Id;

        (await svc.MarkReadAsync(id, true)).IsSuccess.Should().BeTrue();
        (await svc.UnreadCountAsync()).Should().Be(0);

        AsUser(s.ServiceProvider, bob);
        (await s.ServiceProvider.GetRequiredService<INotificationService>().UnreadCountAsync()).Should().Be(1);
    }
}
