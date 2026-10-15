using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Application;
using Nexora.Infrastructure;
using Nexora.Contracts.Setup;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Setup;
using Npgsql;

namespace Nexora.Api.Setup;

/// <summary>
/// Commandes d'exploitation de l'image (finalisation phase 18, §39 « migrations, Docker ») — même binaire, même
/// configuration (appsettings, variables d'environnement, fichiers secrets, configuration d'installation) :
/// <list type="bullet">
/// <item><c>migrate</c> : applique les migrations livrées, sous verrou, avec le compte propriétaire
/// (<c>Database:MigrationConnectionString</c>, à défaut la connexion applicative) ; fixe le mot de passe du rôle
/// applicatif si <c>Database:AppRolePassword</c> est fourni ;</item>
/// <item><c>migrate --status</c> : n'écrit rien ; code 0 à jour, 2 migrations en attente, 3 base incohérente ;</item>
/// <item><c>migrate --realign V084</c> : rejoue une migration idempotente retouchée après application et enregistre
/// sa nouvelle empreinte (procédure du runbook) ;</item>
/// <item><c>bootstrap --company … --activity … --currency … --admin-name … --admin-email …</c> : premier démarrage
/// d'une installation par conteneurs — entreprise et premier administrateur, comme l'assistant ; le mot de passe vient
/// de <c>Bootstrap:AdminPassword</c> (fichier secret ou variable), jamais de la ligne de commande ;</item>
/// <item><c>healthcheck [url]</c> : sonde de vivacité pour le HEALTHCHECK Docker (l'image n'a ni curl ni wget) ;</item>
/// <item><c>reprise-ouvertures [--appliquer]</c> : reprise des soldes d'ouverture saisis sans écriture (audit 2026-10,
/// A-023) ; sans option, n'écrit rien et liste ce qui serait fait.</item>
/// </list>
/// </summary>
public static class CliCommands
{
    public const int Ok = 0, Failed = 1, PendingMigrations = 2, Inconsistent = 3;

    /// <summary>Nom de commande si le premier argument en est une (sinon l'API démarre normalement).</summary>
    public static string? Detect(string[] args)
        => args.Length > 0 && args[0] is "migrate" or "bootstrap" or "healthcheck" or "reprise-ouvertures" ? args[0] : null;

    public static Task<int> RunAsync(string command, string[] args, IConfiguration configuration)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;   // accents lisibles dans une console Windows
        return command switch
        {
            "migrate" => MigrateAsync(args.Skip(1).ToArray(), configuration),
            "bootstrap" => BootstrapAsync(args.Skip(1).ToArray(), configuration),
            "healthcheck" => HealthCheckAsync(args.Skip(1).ToArray(), configuration),
            "reprise-ouvertures" => OpeningRecoveryAsync(args.Skip(1).ToArray(), configuration),
            _ => Task.FromResult(Failed),
        };
    }

    private static async Task<int> MigrateAsync(string[] options, IConfiguration configuration)
    {
        var statusOnly = options.Contains("--status");
        var allowDrift = options.Contains("--allow-drift");
        var connectionString = configuration["Database:MigrationConnectionString"] is { Length: > 0 } owner
            ? owner : configuration["Database:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("migrate : aucune base configurée (Database:MigrationConnectionString ou Database:ConnectionString).");
            return Failed;
        }

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            Console.WriteLine($"migrate : base « {conn.Database} » sur {conn.Host}:{conn.Port}, compte « {new NpgsqlConnectionStringBuilder(connectionString).Username} ».");

            var realignIndex = Array.IndexOf(options, "--realign");
            if (realignIndex >= 0)
            {
                if (realignIndex + 1 >= options.Length)
                {
                    Console.Error.WriteLine("migrate --realign : précisez la version (ex. --realign V084).");
                    return Failed;
                }
                await MigrationRunner.RealignAsync(conn, options[realignIndex + 1]);
                Console.WriteLine($"migrate : {options[realignIndex + 1]} rejouée sans erreur, empreinte réalignée.");
            }

            var status = await MigrationRunner.StatusAsync(conn);
            Report(status);
            if (statusOnly)
                return !status.UpToDate ? PendingMigrations : !status.Consistent ? Inconsistent : Ok;

            if (status.Unknown.Count > 0)
                Console.WriteLine("migrate : ATTENTION — la base contient des migrations inconnues de cette version (retour arrière " +
                                  "de l'application ?). Rien n'est annulé : les migrations sont additives.");

            var roleExisted = await RoleExistsAsync(conn);
            var applied = await MigrationRunner.ApplyPendingAsync(conn, allowDrift: allowDrift,
                progress: file => Console.WriteLine($"  appliquée : {file}"));
            Console.WriteLine(applied == 0 ? "migrate : base déjà à jour." : $"migrate : {applied} migration(s) appliquée(s).");

            await SecureApplicationRoleAsync(conn, roleExisted, configuration["Database:AppRolePassword"]);
            return Ok;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"migrate : ÉCHEC — {ex.Message}");
            return Failed;
        }
    }

    private static void Report(MigrationStatus status)
    {
        Console.WriteLine($"migrate : {status.Applied} appliquée(s) (dernière {status.LatestApplied ?? "aucune"}), " +
                          $"{status.Embedded} livrée(s) (dernière {status.LatestEmbedded}), {status.Pending.Count} en attente.");
        foreach (var p in status.Pending) Console.WriteLine($"  en attente : {p}");
        foreach (var d in status.Drifted) Console.WriteLine($"  MODIFIÉE depuis son application : {d}");
        foreach (var u in status.Unknown) Console.WriteLine($"  inconnue de cette version : {u}");
    }

    private static async Task<bool> RoleExistsAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_app')", conn);
        return await cmd.ExecuteScalarAsync() is true;
    }

    /// <summary>
    /// V020 crée « nexora_app » avec un mot de passe connu (écrit dans la migration). En production, on lui donne le
    /// mot de passe fourni par l'environnement ; sans mot de passe fourni, un rôle tout juste créé ne peut pas se
    /// connecter (comme l'assistant d'installation) — jamais un rôle de connexion au mot de passe public.
    /// </summary>
    private static async Task SecureApplicationRoleAsync(NpgsqlConnection conn, bool roleExisted, string? password)
    {
        if (!string.IsNullOrEmpty(password))
        {
            if (password.Length < 16)
                throw new InvalidOperationException("Database:AppRolePassword : 16 caractères au moins.");
            await using var format = new NpgsqlCommand("SELECT format('ALTER ROLE nexora_app WITH LOGIN PASSWORD %L', @p)", conn);
            format.Parameters.AddWithValue("p", password);
            await using var alter = new NpgsqlCommand((string)(await format.ExecuteScalarAsync())!, conn);
            await alter.ExecuteNonQueryAsync();
            Console.WriteLine("migrate : mot de passe du rôle applicatif nexora_app fixé depuis l'environnement.");
        }
        else if (!roleExisted && await RoleExistsAsync(conn))
        {
            await using var nologin = new NpgsqlCommand("ALTER ROLE nexora_app NOLOGIN PASSWORD NULL", conn);
            await nologin.ExecuteNonQueryAsync();
            Console.WriteLine("migrate : rôle nexora_app créé sans connexion (aucun Database:AppRolePassword fourni).");
        }
    }

    private static async Task<int> BootstrapAsync(string[] options, IConfiguration configuration)
    {
        string? Option(string name)
        {
            var i = Array.IndexOf(options, name);
            return i >= 0 && i + 1 < options.Length ? options[i + 1] : null;
        }

        var connectionString = configuration["Database:MigrationConnectionString"] is { Length: > 0 } owner
            ? owner : configuration["Database:ConnectionString"];
        var password = configuration["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("bootstrap : aucune base configurée.");
            return Failed;
        }
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("bootstrap : mot de passe de l'administrateur absent (Bootstrap:AdminPassword — fichier secret " +
                                    "Bootstrap__AdminPassword ou variable d'environnement), jamais en argument.");
            return Failed;
        }

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            var installer = new SetupInstaller(SetupState.FromConfiguration(configuration, AppContext.BaseDirectory),
                new BcryptPasswordHasher(), NullLogger<SetupInstaller>.Instance);
            var result = await installer.BootstrapAsync(conn,
                new SetupCompanyInput(Option("--company") ?? "", Option("--activity") ?? "", Option("--currency") ?? "HTG"),
                new SetupAdminInput(Option("--admin-name") ?? "", Option("--admin-email") ?? "", password));
            if (result.IsSuccess)
            {
                Console.WriteLine($"bootstrap : entreprise {result.Value} créée, administrateur {Option("--admin-email")} (rôle direction).");
                return Ok;
            }
            if (result.Error!.Code == "setup.database_not_empty")
            {
                Console.WriteLine("bootstrap : base déjà initialisée — rien à faire.");
                return Ok;
            }
            Console.Error.WriteLine($"bootstrap : refusé — {result.Error.Message}");
            return Failed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bootstrap : ÉCHEC — {ex.Message}");
            return Failed;
        }
    }

    /// <summary>
    /// Reprise des soldes d'ouverture par le moteur financier, entreprise par entreprise (comme le planificateur).
    /// Code 0 : rien à reprendre ou tout repris ; 2 : simulation avec des comptes à reprendre ; 1 : au moins un refus.
    /// </summary>
    private static async Task<int> OpeningRecoveryAsync(string[] options, IConfiguration configuration)
    {
        var apply = options.Contains("--appliquer");
        if (string.IsNullOrWhiteSpace(configuration["Database:ConnectionString"]))
        {
            Console.Error.WriteLine("reprise-ouvertures : aucune base configurée (Database:ConnectionString).");
            return Failed;
        }
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(configuration);
            services.AddApplication();
            services.AddInfrastructure(configuration);
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var lines = await scope.ServiceProvider.GetRequiredService<Nexora.Application.Finance.IOpeningBalanceRecovery>()
                .RunAsync(apply, null);

            Console.WriteLine($"reprise-ouvertures : base « {new NpgsqlConnectionStringBuilder(configuration["Database:ConnectionString"]).Database} », " +
                              (apply ? "APPLICATION." : "simulation (rien n'est écrit ; --appliquer pour reprendre)."));
            foreach (var l in lines)
                Console.WriteLine($"  {l.Status,-11} {l.CompanyName} — {l.AccountName} : {l.Amount:N2} {l.Currency}, ouverture au " +
                                  $"{l.OpeningAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC{(l.EntryNumber is { } n ? $", écriture {n}" : "")}{(l.Detail is { } d ? $" — {d}" : "")}");
            if (lines.Count == 0) Console.WriteLine("  aucun solde d'ouverture à reprendre.");
            return lines.Any(l => l.Status == "refuse") ? Failed : !apply && lines.Count > 0 ? PendingMigrations : Ok;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"reprise-ouvertures : ÉCHEC — {ex.Message}");
            return Failed;
        }
    }

    private static async Task<int> HealthCheckAsync(string[] options, IConfiguration configuration)
    {
        var port = (configuration["ASPNETCORE_HTTP_PORTS"] ?? "8080").Split(';', ',')[0].Trim();
        var url = options.FirstOrDefault() ?? $"http://localhost:{port}/health/live";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode ? Ok : Failed;
        }
        catch
        {
            return Failed;
        }
    }
}
