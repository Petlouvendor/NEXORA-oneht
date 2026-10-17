using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application;
using Nexora.Application.Abstractions;
using Nexora.Infrastructure;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Prépare une base de test clonée depuis la base migrée locale, puis un ServiceProvider
/// câblé sur Infrastructure + Application. Le contexte tenant est fixé sur la Pharmacie ABC.
///
/// Connexion admin via NEXORA_IT_ADMIN (défaut : postgres local). En CI (Docker),
/// définir NEXORA_IT_ADMIN sur la base éphémère.
/// </summary>
public sealed class FinanceEngineFixture : IAsyncLifetime
{
    /// <summary>
    /// Base modèle clonée par la fixture : <c>nexora</c> par défaut ; <c>NEXORA_IT_TEMPLATE</c> désigne un autre modèle —
    /// par exemple un clone jetable auquel on a appliqué une migration en attente, pour la tester sans toucher à la
    /// base de développement (audit 2026-10).
    /// </summary>
    private static readonly string SourceDb =
        Environment.GetEnvironmentVariable("NEXORA_IT_TEMPLATE") is { Length: > 0 } template ? template : "nexora";
    private readonly string _testDb = $"nexora_fin_it_{DateTime.UtcNow:HHmmss}";

    public ServiceProvider Provider { get; private set; } = default!;
    public string TestConnectionString { get; private set; } = "";
    public Guid CompanyId { get; } = Guid.Parse("dce00000-0000-0000-0000-0000000000c1"); // Pharmacie ABC
    public Guid UserId { get; } = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private string AdminConnString =>
        Environment.GetEnvironmentVariable("NEXORA_IT_ADMIN")
        ?? "Host=localhost;Port=5432;Username=postgres;Password=Admin@2023;Database=postgres";

    public async Task InitializeAsync()
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(AdminConnString);
        // Le clonage de la base modèle (≈ 50 Mo) dépasse couramment les 30 s par défaut de Npgsql sur un poste chargé :
        // toute la suite échouait alors en 1 ms (audit 2026-10 éd. 2, T-003). Un délai fourni plus long est conservé.
        adminBuilder.CommandTimeout = Math.Max(adminBuilder.CommandTimeout, 600);
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await Exec(admin, $"DROP DATABASE IF EXISTS {_testDb} WITH (FORCE);");

            // `CREATE DATABASE ... TEMPLATE` échoue si une autre connexion tient la source
            // (ex. les tests API tournant en parallèle) : on coupe les connexions puis on réessaie.
            for (var attempt = 1; ; attempt++)
            {
                await Exec(admin, $"""
                    SELECT pg_terminate_backend(pid) FROM pg_stat_activity
                    WHERE datname = '{SourceDb}' AND pid <> pg_backend_pid();
                    """);
                try
                {
                    await Exec(admin, $"CREATE DATABASE {_testDb} TEMPLATE {SourceDb};");
                    break;
                }
                catch (PostgresException ex) when (ex.SqlState == "55006" && attempt < 10)
                {
                    await Task.Delay(250 * attempt);
                }
            }
        }

        var testConn = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Database = _testDb,
            SearchPath = "nexora,public",
            IncludeErrorDetail = true,
            // Les tests gardent le délai d'origine : seul le clonage a besoin du délai allongé.
            CommandTimeout = new NpgsqlConnectionStringBuilder(AdminConnString).CommandTimeout,
        }.ConnectionString;
        TestConnectionString = testConn;

        // Exploitant de la plateforme (audit 2026-10, A-031) : plusieurs tests créent des entreprises indépendantes
        // dans cette base partagée — l'installation y héberge donc plusieurs clients, et les opérations de portée
        // plateforme (maintenance, tâches, taux de change…) y sont réservées à l'exploitant. L'utilisateur de test
        // « direction » reçoit ce rôle, limité aux permissions d'exploitation ; les tests de refus utilisent un
        // directeur d'entreprise ordinaire.
        await using (var test = new NpgsqlConnection(testConn))
        {
            await test.OpenAsync();
            await Exec(test, $"""
                INSERT INTO nexora.role (id, company_id, name, description, system_role, status)
                VALUES ('{PlatformOperatorRole}', NULL, 'platform_test_operator', 'Exploitant de plateforme (tests)', false, 'active');
                INSERT INTO nexora.role_permission (role_id, permission_id)
                SELECT '{PlatformOperatorRole}', id FROM nexora.permission
                WHERE code IN ('operations.view', 'operations.manage', 'operations.correct_data', 'finance.update');
                INSERT INTO nexora.access_scope (id, scope_type) VALUES ('{PlatformOperatorScope}', 'PLATFORM');
                INSERT INTO nexora.user_role (user_id, role_id, scope_id) VALUES ('{UserId}', '{PlatformOperatorRole}', '{PlatformOperatorScope}');
                """);

            // Taux non entier sur une seule journée (audit 2026-10 éd. 2, A-041 / A-052) : les taux de démonstration
            // sont entiers (132, 135), et un montant en centimes multiplié par un taux entier ne produit jamais de
            // demi-centime — les défauts d'arrondi en devise restaient invisibles. Le taux d'avant est rétabli le
            // lendemain, les autres tests ne voient donc aucun changement.
            await Exec(test, $"""
                WITH usd AS (SELECT id FROM nexora.currency WHERE code = 'USD'),
                     htg AS (SELECT id FROM nexora.currency WHERE code = 'HTG'),
                     previous AS (
                         SELECT r.rate FROM nexora.exchange_rate r, usd, htg
                         WHERE r.base_currency_id = usd.id AND r.quote_currency_id = htg.id
                           AND r.effective_at <= '{FractionalUsdRateDay.AddDays(1):yyyy-MM-dd} 00:00:00+00'
                         ORDER BY r.effective_at DESC LIMIT 1)
                INSERT INTO nexora.exchange_rate (base_currency_id, quote_currency_id, rate, effective_at, source)
                SELECT usd.id, htg.id, {FractionalUsdRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}::numeric,
                       '{FractionalUsdRateDay:yyyy-MM-dd} 00:00:00+00'::timestamptz, 'test'
                FROM usd, htg
                UNION ALL
                SELECT usd.id, htg.id, previous.rate, '{FractionalUsdRateDay.AddDays(1):yyyy-MM-dd} 00:00:00+00'::timestamptz, 'test'
                FROM usd, htg, previous;
                """);
        }

        Provider = BuildProvider(testConn);
    }

    /// <summary>Taux USD → HTG non entier, en vigueur le seul jour <see cref="FractionalUsdRateDay"/>.</summary>
    public const decimal FractionalUsdRate = 132.5m;

    /// <summary>Jour (UTC, 10 h) où <see cref="FractionalUsdRate"/> s'applique ; la veille et le lendemain gardent le taux d'origine.</summary>
    public static readonly DateTimeOffset FractionalUsdRateDay = new(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid PlatformOperatorRole = Guid.Parse("dce00000-0000-0000-0000-00000000f0a1");
    private static readonly Guid PlatformOperatorScope = Guid.Parse("dce00000-0000-0000-0000-00000000f0a2");

    /// <summary>Directeur d'entreprise ordinaire (rôle owner_direction sur la seule entreprise), sans portée plateforme.</summary>
    public async Task<Guid> NewCompanyDirectorAsync(Guid companyId)
    {
        var id = Guid.NewGuid();
        await using var c = new NpgsqlConnection(TestConnectionString);
        await c.OpenAsync();
        await Exec(c, $"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{id}', 'dir-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), 'Directeur d''entreprise', 'active');
            INSERT INTO nexora.user_role (user_id, role_id, scope_id)
            SELECT '{id}', r.id, sc.id FROM nexora.role r, nexora.access_scope sc
            WHERE r.name = 'owner_direction' AND r.system_role AND sc.scope_type = 'COMPANY' AND sc.company_id = '{companyId}'
            LIMIT 1;
            """);
        return id;
    }

    /// <summary>
    /// Fournisseur connecté avec le rôle applicatif réel <c>nexora_app</c> (sans superutilisateur ni
    /// BYPASSRLS) : le fournisseur par défaut se connecte en administrateur et contourne donc toute
    /// politique RLS. À utiliser pour prouver une isolation portée par la base (ex. succursales).
    /// </summary>
    public ServiceProvider CreateAppRoleProvider()
        => BuildProvider(new NpgsqlConnectionStringBuilder(TestConnectionString)
        {
            Username = "nexora_app",
            Password = Environment.GetEnvironmentVariable("NEXORA_IT_APP_PASSWORD") ?? "nexora_app_dev",
        }.ConnectionString);

    /// <summary>Fournisseur complété par des enregistrements de test (ex. faux serveur d'un fournisseur de paiement).</summary>
    public ServiceProvider CreateProviderWith(Action<IServiceCollection> extra, IDictionary<string, string?>? settings = null)
        => BuildProvider(TestConnectionString, extra, settings);

    private ServiceProvider BuildProvider(string connectionString, Action<IServiceCollection>? extra = null,
        IDictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = connectionString,
            ["Jwt:SigningKey"] = "integration-test-signing-key-000000000000000000",
        };
        foreach (var (k, v) in settings ?? new Dictionary<string, string?>()) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        // Contexte tenant fixe pour les tests.
        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TestTenantContext();
            t.SetUser(UserId);
            t.SetScope([CompanyId], [], CompanyId);
            return t;
        });
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await Provider.DisposeAsync();
        var adminBuilder = new NpgsqlConnectionStringBuilder(AdminConnString);
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await Exec(admin, $"DROP DATABASE IF EXISTS {_testDb} WITH (FORCE);");
    }

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}

internal sealed class TestTenantContext : ITenantContext
{
    private readonly List<Guid> _companies = [];
    private readonly List<Guid> _groups = [];
    public Guid? UserId { get; private set; }
    public IReadOnlyCollection<Guid> CompanyIds => _companies;
    public IReadOnlyCollection<Guid> GroupIds => _groups;
    public Guid? ActiveCompanyId { get; private set; }
    private int _systemDepth;
    public bool BypassRls => _systemDepth > 0;
    public IReadOnlyCollection<string>? ApiScopes { get; private set; }
    public IReadOnlyCollection<Guid>? EstablishmentIds { get; private set; }
    public event Action? SystemScopeChanged;

    public void SetEstablishments(IEnumerable<Guid>? establishmentIds)
        => EstablishmentIds = establishmentIds?.Distinct().ToArray();

    public void SetUser(Guid userId) => UserId = userId;
    public void SetApiScopes(IEnumerable<string>? scopes) => ApiScopes = scopes?.ToHashSet();
    public void SetScope(IEnumerable<Guid> companyIds, IEnumerable<Guid> groupIds, Guid? activeCompanyId)
    {
        _companies.Clear(); _companies.AddRange(companyIds);
        _groups.Clear(); _groups.AddRange(groupIds);
        ActiveCompanyId = activeCompanyId;
    }
    public IDisposable BeginSystemScope()
    {
        if (++_systemDepth == 1) SystemScopeChanged?.Invoke();
        return new Reset(this);
    }
    private sealed class Reset(TestTenantContext o) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--o._systemDepth == 0) o.SystemScopeChanged?.Invoke();
        }
    }
}

[CollectionDefinition("finance")]
public sealed class FinanceCollection : ICollectionFixture<FinanceEngineFixture>;
