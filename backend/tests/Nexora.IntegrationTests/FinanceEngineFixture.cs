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
    private const string SourceDb = "nexora";
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
        }.ConnectionString;
        TestConnectionString = testConn;

        Provider = BuildProvider(testConn);
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

    private ServiceProvider BuildProvider(string connectionString)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = connectionString,
            ["Jwt:SigningKey"] = "integration-test-signing-key-000000000000000000",
        }).Build();

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
    public bool BypassRls { get; private set; }
    public IReadOnlyCollection<string>? ApiScopes { get; private set; }
    public IReadOnlyCollection<Guid>? EstablishmentIds { get; private set; }

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
        BypassRls = true;
        return new Reset(this);
    }
    private sealed class Reset(TestTenantContext o) : IDisposable
    {
        public void Dispose() => o.BypassRls = false;
    }
}

[CollectionDefinition("finance")]
public sealed class FinanceCollection : ICollectionFixture<FinanceEngineFixture>;
