using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Nexora.Contracts.Auth;
using Nexora.Contracts.Setup;
using Npgsql;

namespace Nexora.ApiTests;

/// <summary>
/// Résumé fonctionnel §20 — INSTALLATION → POSTGRESQL → TEST → MIGRATIONS → ENTREPRISE → ADMIN →
/// CONFIGURATION → APPLICATION. L'API démarre sans base : seul l'assistant répond (jeton requis),
/// l'installation se fait sur une base NEUVE créée pour le test, puis un second hôte — le « redémarrage »
/// — lit la configuration écrite : l'administrateur se connecte et trouve une caisse prête à vendre.
/// </summary>
public sealed class SetupWizardTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Token = "jeton-installation-de-test-0123456789";
    private readonly string _database = $"nexora_setup_it_{Guid.NewGuid():N}"[..30];
    // Base « déjà installée » propre au test : la base de développement `nexora` est clonée (et ses
    // connexions coupées) par les tests d'intégration qui tournent en parallèle.
    private readonly string _occupied = $"nexora_setup_busy_{Guid.NewGuid():N}"[..32];
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), $"nexora-setup-{Guid.NewGuid():N}");
    private string ConfigPath => Path.Combine(_configDir, "nexora.install.json");

    private static NpgsqlConnectionStringBuilder Admin => new(
        Environment.GetEnvironmentVariable("NEXORA_IT_ADMIN")
        ?? "Host=localhost;Port=5432;Username=postgres;Password=Admin@2023;Database=postgres");

    private WebApplicationFactory<Program> Host(bool setupMode) => factory.WithWebHostBuilder(b =>
    {
        if (setupMode) b.UseSetting("Database:ConnectionString", "");
        b.UseSetting("Install:ConfigPath", ConfigPath);
        b.UseSetting("Install:SetupToken", Token);
        b.UseSetting("Install:AutoRestart", "false");
    });

    private DatabaseSetupRequest Db(string? database = null, string? password = null) => new(
        Admin.Host!, Admin.Port, database ?? _database, Admin.Username!, password ?? Admin.Password!, "Prefer");

    private static InstallRequest Install(DatabaseSetupRequest db) => new(db,
        new SetupCompanyInput("Autre boutique", "boutique", "HTG"),
        new SetupAdminInput("Autre administrateur", "autre@boutique.test", "Installation2026!"));

    private static HttpRequestMessage Post(string url, object? body, string? token = Token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Add("X-Setup-Token", token);
        return request;
    }

    private static async Task<string?> Title(HttpResponseMessage r)
        => (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("title", out var t) ? t.GetString() : null;

    [Fact]
    public async Task Fresh_server_is_installed_through_the_wizard_then_the_admin_can_sell()
    {
        using var setupHost = Host(setupMode: true);
        var client = setupHost.CreateClient();
        // 72 migrations : sous la charge des tests d'intégration en parallèle, bien plus que les 100 s par défaut.
        client.Timeout = TimeSpan.FromMinutes(10);

        // Mode installation : l'assistant est ouvert, le reste de l'API est fermé (503).
        (await client.GetFromJsonAsync<SetupStatusDto>("/api/v1/setup/status"))
            .Should().Be(new SetupStatusDto(SetupRequired: true, Installed: false, RestartRequired: false));
        var closed = await client.GetAsync("/api/v1/me");
        closed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Title(closed)).Should().Be("setup.required");

        // Jeton exigé : absent ou faux → 403 (jamais 401, qui déconnecterait l'interface).
        (await client.SendAsync(Post("/api/v1/setup/verify-token", null, token: null))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post("/api/v1/setup/verify-token", null, token: "mauvais-jeton-000000"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post("/api/v1/setup/verify-token", null))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Test de connexion : mauvais mot de passe décrit, base existante non vide refusée, base neuve prête.
        var badPassword = await (await client.SendAsync(Post("/api/v1/setup/database/test", Db(password: "faux-mot-de-passe"))))
            .Content.ReadFromJsonAsync<DatabaseTestResultDto>();
        badPassword!.Connected.Should().BeFalse();
        badPassword.Messages.Should().ContainSingle().Which.Should().Contain("Authentification refusée");

        var existing = await (await client.SendAsync(Post("/api/v1/setup/database/test", Db(database: _occupied))))
            .Content.ReadFromJsonAsync<DatabaseTestResultDto>();
        existing!.AlreadyInitialized.Should().BeTrue("la base contient déjà une entreprise Nexora One");
        existing.Ready.Should().BeFalse();

        var fresh = await (await client.SendAsync(Post("/api/v1/setup/database/test", Db())))
            .Content.ReadFromJsonAsync<DatabaseTestResultDto>();
        fresh!.Should().Match<DatabaseTestResultDto>(t => t.Connected && t.Ready && !t.DatabaseExists);

        // Règles de saisie vérifiées avant de toucher la base.
        var weak = await client.SendAsync(Post("/api/v1/setup/install", new InstallRequest(Db(),
            new SetupCompanyInput("Boutique Installée", "boutique", "HTG"),
            new SetupAdminInput("Administrateur", "admin@boutique.test", "court"))));
        weak.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Title(weak)).Should().Be("auth.password_policy");

        // Installation complète.
        const string password = "Installation2026!";
        var install = await client.SendAsync(Post("/api/v1/setup/install", new InstallRequest(Db(),
            new SetupCompanyInput("Boutique Installée", "boutique", "HTG"),
            new SetupAdminInput("Administrateur Boutique", "Admin@Boutique.test", password),
            new SetupSettingsInput(RequireCashSession: true, CashVarianceThreshold: 250, MaxDiscountPercent: 10))));
        install.StatusCode.Should().Be(HttpStatusCode.OK, await install.Content.ReadAsStringAsync());
        var installed = (await install.Content.ReadFromJsonAsync<InstallResultDto>())!;
        installed.MigrationsApplied.Should().BeGreaterThan(70);
        installed.AdminEmail.Should().Be("admin@boutique.test");

        // L'assistant se désactive : plus de seconde installation, redémarrage demandé.
        (await client.GetFromJsonAsync<SetupStatusDto>("/api/v1/setup/status"))!.RestartRequired.Should().BeTrue();
        (await client.SendAsync(Post("/api/v1/setup/install", Install(Db())))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Title(await client.GetAsync("/api/v1/me"))).Should().Be("setup.restart_required");

        // Configuration écrite : rôle dédié (pas le superutilisateur d'installation), clés générées.
        var written = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath)).RootElement;
        var runtime = new NpgsqlConnectionStringBuilder(written.GetProperty("Database").GetProperty("ConnectionString").GetString());
        runtime.Username.Should().StartWith("nexora_rt_").And.NotBe(Admin.Username);
        runtime.Database.Should().Be(_database);
        written.GetProperty("Jwt").GetProperty("SigningKey").GetString().Should().HaveLength(64);
        Convert.FromBase64String(written.GetProperty("Security").GetProperty("MfaEncryptionKey").GetString()!).Should().HaveCount(32);

        await using (var check = new NpgsqlConnection(runtime.ConnectionString))
        {
            await check.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = current_user", check);
            ((bool)(await cmd.ExecuteScalarAsync())!).Should().BeFalse("le rôle applicatif reste soumis à RLS");
        }

        // « Redémarrage » : un nouvel hôte lit le fichier écrit ; l'assistant est désactivé.
        using var appHost = Host(setupMode: false);
        var app = appHost.CreateClient();
        (await app.GetFromJsonAsync<SetupStatusDto>("/api/v1/setup/status"))
            .Should().Be(new SetupStatusDto(SetupRequired: false, Installed: true, RestartRequired: false));
        (await app.SendAsync(Post("/api/v1/setup/verify-token", null))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var login = await app.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("admin@boutique.test", password, installed.CompanyId));
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        var auth = (await login.Content.ReadFromJsonAsync<AuthTokenResponse>())!;
        app.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        app.DefaultRequestHeaders.Add("X-Company-Id", installed.CompanyId.ToString());

        // Référentiel prêt à vendre (V072) : caisse avec compte financier et emplacement de stock.
        var registers = await app.GetFromJsonAsync<JsonElement>("/api/v1/pos/registers");
        registers.GetArrayLength().Should().Be(1);
        var register = registers[0];
        register.GetProperty("name").GetString().Should().Be("Caisse 1");
        register.GetProperty("financialAccountId").ValueKind.Should().Be(JsonValueKind.String);
        register.GetProperty("inventoryLocationId").ValueKind.Should().Be(JsonValueKind.String);

        // Paramètres saisis dans l'assistant.
        (await app.GetFromJsonAsync<JsonElement>("/api/v1/pos/me/limits"))
            .GetProperty("discountLimitPercent").GetDecimal().Should().Be(10, "plafond entreprise saisi dans l'assistant");
    }

    [Fact]
    public async Task Configured_application_keeps_the_wizard_closed()
    {
        var client = factory.CreateClient();
        (await client.GetFromJsonAsync<SetupStatusDto>("/api/v1/setup/status"))
            .Should().Be(new SetupStatusDto(SetupRequired: false, Installed: true, RestartRequired: false));
        (await client.SendAsync(Post("/api/v1/setup/database/test", Db()))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.SendAsync(Post("/api/v1/setup/install", Install(Db())))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(Admin.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_occupied}", admin);
            await create.ExecuteNonQueryAsync();
        }
        var occupied = new NpgsqlConnectionStringBuilder(Admin.ConnectionString) { Database = _occupied, Pooling = false };
        await using var conn = new NpgsqlConnection(occupied.ConnectionString);
        await conn.OpenAsync();
        await using var seed = new NpgsqlCommand("""
            CREATE SCHEMA nexora;
            CREATE TABLE nexora.company (id uuid PRIMARY KEY, name text NOT NULL);
            INSERT INTO nexora.company VALUES (gen_random_uuid(), 'Entreprise existante');
            """, conn);
        await seed.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        string? role = null;
        if (File.Exists(ConfigPath))
            role = new NpgsqlConnectionStringBuilder(JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath))
                .RootElement.GetProperty("Database").GetProperty("ConnectionString").GetString()).Username;

        // Sous la charge des tests d'intégration (créations de bases en rafale), DROP DATABASE peut
        // attendre un checkpoint bien au-delà des 30 s par défaut : délai long, étapes indépendantes.
        var cleanup = new NpgsqlConnectionStringBuilder(Admin.ConnectionString) { CommandTimeout = 300, Pooling = false };
        await using var admin = new NpgsqlConnection(cleanup.ConnectionString);
        await admin.OpenAsync();
        var statements = new List<string> { $"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", $"DROP DATABASE IF EXISTS {_occupied} WITH (FORCE)" };
        if (role is not null) statements.Add($"DROP ROLE IF EXISTS {role}");
        var failures = new List<Exception>();
        foreach (var sql in statements)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(sql, admin);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true);
        if (failures.Count > 0) throw new AggregateException("Nettoyage incomplet du serveur de test.", failures);
    }
}
