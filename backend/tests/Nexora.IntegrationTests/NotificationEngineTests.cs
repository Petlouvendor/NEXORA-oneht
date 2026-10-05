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
        // Canal de test en panne temporaire : relance avec délai, puis rejet après le nombre maximal de tentatives.
        await using var sp = fx.CreateProviderWith(sv => sv.AddSingleton<INotificationChannel>(new FlakyChannel()));
        await using var s = sp.CreateAsyncScope();
        var uid = await NewUser();
        AsUser(s.ServiceProvider, uid);
        var dispatcher = s.ServiceProvider.GetRequiredService<INotificationDispatcher>();

        var outboxId = Guid.NewGuid();
        await Sql($"""
            INSERT INTO nexora.notification_outbox (id, company_id, user_id, channel, category, subject, body, address, max_attempts, next_attempt_at)
            VALUES ('{outboxId}', '{Company}', '{uid}', 'whatsapp', 'low_stock', 'x', 'y', '+50937000000', 2, now() - interval '1 minute')
            """);

        await dispatcher.DispatchOneAsync(outboxId);
        (await Scalar<string>($"SELECT status FROM nexora.notification_outbox WHERE id='{outboxId}'")).Should().Be("failed");

        await Sql($"UPDATE nexora.notification_outbox SET next_attempt_at = now() - interval '1 minute' WHERE id='{outboxId}'");
        await dispatcher.DispatchOneAsync(outboxId);

        (await Scalar<string>($"SELECT status FROM nexora.notification_outbox WHERE id='{outboxId}'")).Should().Be("dead");
        (await Scalar<short>($"SELECT attempts FROM nexora.notification_outbox WHERE id='{outboxId}'")).Should().Be((short)2);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.notification_delivery_log WHERE outbox_id='{outboxId}'")).Should().Be(2,
            "chaque tentative est journalisée");
        (await Scalar<string>($"SELECT provider FROM nexora.notification_outbox WHERE id='{outboxId}'")).Should().Be("panne simulée");
    }

    /// <summary>Canal dont le fournisseur est momentanément indisponible (prend la place de WhatsApp dans ce test).</summary>
    private sealed class FlakyChannel : INotificationChannel
    {
        public string Channel => "whatsapp";
        public string ProviderName => "panne simulée";
        public bool IsConfigured => true;
        public string ConfigurationSummary => "Canal de test";
        public Task<ChannelSendResult> SendAsync(Nexora.Contracts.Notifications.OutboxMessageDto message, CancellationToken ct = default)
            => Task.FromResult(ChannelSendResult.Retry("Service momentanément indisponible."));
        public Task<Nexora.Contracts.Notifications.ChannelTestResultDto> TestAsync(CancellationToken ct = default)
            => Task.FromResult(new Nexora.Contracts.Notifications.ChannelTestResultDto("whatsapp", "UNAVAILABLE", "panne", 0));
    }

    // ---- Finalisation phase 16 : sans fournisseur, rien n'est « envoyé » et rien n'est escaladé ----
    [Fact]
    public async Task Without_a_provider_nothing_is_reported_as_sent_and_nothing_is_escalated()
    {
        await using var s = Scope();
        var uid = await NewUser();
        AsUser(s.ServiceProvider, uid);
        var dispatcher = s.ServiceProvider.GetRequiredService<INotificationDispatcher>();
        var admin = s.ServiceProvider.GetRequiredService<INotificationDeliveryAdmin>();

        var ids = new Dictionary<string, Guid>();
        foreach (var (channel, address) in new[] { ("webhook", "https://exemple.test/hook"), ("email", "client@exemple.test"), ("sms", "+50937000000"), ("push", "jeton-appareil-0001") })
        {
            var id = Guid.NewGuid();
            ids[channel] = id;
            await Sql($"""
                INSERT INTO nexora.notification_outbox (id, company_id, user_id, channel, category, subject, body, address, next_attempt_at)
                VALUES ('{id}', '{Company}', '{uid}', '{channel}', 'low_stock', 'x', 'y', '{address}', now() - interval '1 minute')
                """);
        }
        var escalationsBefore = await Scalar<long>("SELECT COUNT(*) FROM nexora.notification WHERE category = 'delivery_failed'");
        foreach (var id in ids.Values) await dispatcher.DispatchOneAsync(id);

        foreach (var (channel, id) in ids)
        {
            (await Scalar<string>($"SELECT status FROM nexora.notification_outbox WHERE id='{id}'")).Should().Be("not_configured",
                $"le canal {channel} n'a pas de fournisseur configuré : le webhook se disait « envoyé », les autres partaient en rejet escaladé");
            (await Scalar<string>($"SELECT last_error FROM nexora.notification_outbox WHERE id='{id}'")).Should().Contain("n'a pas été envoyé");
        }
        (await Scalar<long>("SELECT COUNT(*) FROM nexora.notification WHERE category = 'delivery_failed'")).Should().Be(escalationsBefore);

        // Fournisseurs : état honnête, et relance refusée tant que rien n'est configuré.
        var providers = await admin.ProvidersAsync();
        providers.Select(p => p.Channel).Should().Contain(["email", "sms", "push", "webhook"]);
        providers.Should().OnlyContain(p => !p.Configured);
        providers.Single(p => p.Channel == "email").NotConfiguredWaiting.Should().BeGreaterThan(0);
        foreach (var ch in new[] { "email", "sms", "push", "webhook" })
            (await admin.TestChannelAsync(ch)).Value!.Status.Should().Be("NOT_CONFIGURED");
        (await admin.RetryAsync(ids["email"])).Error!.Code.Should().Be("notifications.provider_not_configured");
        (await admin.RequeueNotConfiguredAsync("sms")).Error!.Code.Should().Be("notifications.provider_not_configured");

        var entry = (await admin.ListAsync("not_configured", "email")).Single(e => e.Id == ids["email"]);
        entry.AddressMasked.Should().Be("cl…@exemple.test", "l'adresse est masquée dans le journal");
        (await admin.LogAsync(ids["email"])).Value!.Single().Status.Should().Be("not_configured");
        (await admin.SummaryAsync()).NotConfigured.Should().BeGreaterThanOrEqualTo(4);

        (await admin.SendTestAsync(new Nexora.Contracts.Notifications.SendTestMessageRequest("sms", "50937"))).Error!.Code
            .Should().Be("notifications.test_address");
        var test = (await admin.SendTestAsync(new Nexora.Contracts.Notifications.SendTestMessageRequest("email", "admin@exemple.test"))).Value!;
        test.Status.Should().Be("not_configured", "un message de test sans fournisseur n'est pas « envoyé »");
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

    // ---- Finalisation phase 16 : canal e-mail testé contre un vrai dialogue SMTP (serveur local) ----

    /// <summary>Serveur SMTP minimal : accueil, EHLO avec AUTH, authentification (mot de passe « bon »), MAIL/RCPT/DATA.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }
        public List<string> Received { get; } = [];

        public FakeSmtpServer()
        {
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(System.Net.Sockets.TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
            var writer = new StreamWriter(stream, System.Text.Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake.smtp ESMTP");
            var inData = false;
            var data = new System.Text.StringBuilder();
            while (await reader.ReadLineAsync() is { } line)
            {
                if (inData)
                {
                    if (line == ".") { inData = false; lock (Received) Received.Add(data.ToString()); await writer.WriteLineAsync("250 OK queued"); }
                    else data.AppendLine(line);
                    continue;
                }
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) { await writer.WriteLineAsync("250-fake.smtp"); await writer.WriteLineAsync("250 AUTH PLAIN LOGIN"); }
                else if (cmd.StartsWith("AUTH PLAIN"))
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Split(' ')[2]));
                    await writer.WriteLineAsync(decoded.EndsWith("\0bon") ? "235 2.7.0 Authentication successful" : "535 5.7.8 Authentication credentials invalid");
                }
                else if (cmd.StartsWith("MAIL FROM") || cmd.StartsWith("RCPT TO")) await writer.WriteLineAsync("250 OK");
                else if (cmd == "DATA") { inData = true; await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>"); }
                else if (cmd == "QUIT") { await writer.WriteLineAsync("221 Bye"); return; }
                else await writer.WriteLineAsync("250 OK");
            }
        }

        public ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); return ValueTask.CompletedTask; }
    }

    private ServiceProvider SmtpProvider(int port, string password) => fx.CreateProviderWith(_ => { }, new Dictionary<string, string?>
    {
        ["Notifications:Smtp:Enabled"] = "true",
        ["Notifications:Smtp:Host"] = "127.0.0.1",
        ["Notifications:Smtp:Port"] = port.ToString(),
        ["Notifications:Smtp:UserName"] = "nexora",
        ["Notifications:Smtp:Password"] = password,
        ["Notifications:Smtp:FromAddress"] = "no-reply@exemple.test",
    });

    [Fact]
    public async Task Email_channel_test_talks_to_a_real_smtp_server()
    {
        await using var server = new FakeSmtpServer();
        var uid = await NewUser();

        await using (var bad = SmtpProvider(server.Port, "mauvais"))
        await using (var s = bad.CreateAsyncScope())
        {
            AsUser(s.ServiceProvider, uid);
            var r = (await s.ServiceProvider.GetRequiredService<INotificationDeliveryAdmin>().TestChannelAsync("email")).Value!;
            r.Status.Should().Be("AUTHENTICATION_FAILED");
        }

        await using (var good = SmtpProvider(server.Port, "bon"))
        await using (var s = good.CreateAsyncScope())
        {
            AsUser(s.ServiceProvider, uid);
            var admin = s.ServiceProvider.GetRequiredService<INotificationDeliveryAdmin>();
            (await admin.TestChannelAsync("email")).Value!.Status.Should().Be("CONNECTED");
            server.Received.Should().BeEmpty("le test de connexion n'envoie aucun message");

            var providers = await admin.ProvidersAsync();
            var email = providers.Single(p => p.Channel == "email");
            email.Configured.Should().BeTrue();
            email.ConfigurationSummary.Should().Contain("127.0.0.1").And.NotContain("bon", "aucun secret affiché");
            email.LastTestStatus.Should().Be("CONNECTED");

            var sent = (await admin.SendTestAsync(new Nexora.Contracts.Notifications.SendTestMessageRequest("email", "destinataire@exemple.test"))).Value!;
            sent.Status.Should().Be("sent");
            sent.Provider.Should().Be("SMTP");
            server.Received.Should().ContainSingle().Which.Should().Contain("Test d'envoi NEXORA ONE");
        }

        await using (var down = SmtpProvider(1, "bon"))
        await using (var s = down.CreateAsyncScope())
        {
            AsUser(s.ServiceProvider, uid);
            (await s.ServiceProvider.GetRequiredService<INotificationDeliveryAdmin>().TestChannelAsync("email")).Value!.Status.Should().Be("UNAVAILABLE");
        }
    }
}
