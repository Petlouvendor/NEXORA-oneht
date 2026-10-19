using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Setup;
using Nexora.Contracts.Setup;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Group;
using Npgsql;

namespace Nexora.Infrastructure.Setup;

/// <summary>
/// Assistant d'installation (résumé fonctionnel §20) : INSTALLATION → POSTGRESQL → TEST → MIGRATIONS →
/// ENTREPRISE → ADMIN → CONFIGURATION → APPLICATION.
/// <list type="bullet">
/// <item>connexion directe Npgsql avec le compte d'installation saisi (le contexte EF n'a pas encore de base) ;</item>
/// <item>migrations embarquées dans l'assembly (mêmes fichiers et même suivi <c>schema_migration</c> que
/// <c>database/scripts/apply.sh</c>), chacune dans sa transaction ;</item>
/// <item>entreprise + référentiel prêt à vendre (<c>nexora.bootstrap_company_defaults</c>, V072), paramètres,
/// premier administrateur (rôle « owner_direction » au niveau entreprise) et un rôle de connexion dédié,
/// membre de <c>nexora_app</c> — donc sans BYPASSRLS — le tout dans UNE transaction ;</item>
/// <item>configuration écrite dans <see cref="SetupState.ConfigPath"/> : chaîne de connexion du rôle
/// applicatif, clé de signature JWT et clé de chiffrement MFA générées (jamais de secret dans le code).</item>
/// </list>
/// Une base contenant déjà des entreprises ou des utilisateurs est refusée.
/// </summary>
public sealed partial class SetupInstaller(
    SetupState state, IPasswordHasher hasher, ILogger<SetupInstaller> logger) : ISetupInstaller
{
    public static readonly IReadOnlyList<SetupChoiceDto> ActivityChoices =
    [
        new("supermarket", "Supermarché"), new("grocery", "Épicerie / alimentation"), new("pharmacy", "Pharmacie"),
        new("restaurant", "Restaurant"), new("boutique", "Boutique"), new("hotel", "Hôtel"),
        new("hardware_store", "Quincaillerie"), new("wholesaler", "Grossiste"), new("distributor", "Distributeur"),
        new("services", "Services"), new("production", "Production"), new("ecommerce", "Commerce en ligne"),
        new("agriculture", "Agriculture"), new("other", "Autre"),
    ];

    /// <summary>Devises semées par V021.</summary>
    public static readonly IReadOnlyList<SetupChoiceDto> CurrencyChoices =
    [
        new("HTG", "Gourde haïtienne (HTG)"), new("USD", "Dollar américain (USD)"),
        new("EUR", "Euro (EUR)"), new("CAD", "Dollar canadien (CAD)"),
    ];

    public static readonly IReadOnlyList<string> SslModes = ["Prefer", "Require", "VerifyFull", "Disable"];

    private const int MinServerVersion = 150000;   // UNIQUE NULLS NOT DISTINCT (V0xx)
    private const string InitialAdminRole = "owner_direction";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex DatabaseName();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailShape();

    // =====================================================================================
    // Test de connexion
    // =====================================================================================
    public async Task<Result<DatabaseTestResultDto>> TestDatabaseAsync(DatabaseSetupRequest request, CancellationToken ct = default)
    {
        if (ValidateConnection(request) is { } invalid) return invalid;
        return Result.Success(await ProbeAsync(request, ct));
    }

    private sealed class Probe
    {
        public bool Connected;
        public bool Failed;          // une erreur pendant la sonde : jamais « prête »
        public string? Version;
        public int VersionNum;
        public bool DatabaseExists, AlreadyInitialized, IsSuperuser, CanCreateRole, CanCreateDatabase, CanCreateInDatabase;
        public readonly List<string> Messages = [];
    }

    private async Task<DatabaseTestResultDto> ProbeAsync(DatabaseSetupRequest request, CancellationToken ct)
    {
        var p = new Probe();
        var database = request.Database.Trim();
        try
        {
            await using var conn = await OpenAsync(request, database, ct);
            p.DatabaseExists = true;
            await ReadServerAsync(conn, p, ct);
            await ReadDatabaseAsync(conn, p, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            // Base absente : on interroge le serveur via la base de maintenance.
            try
            {
                await using var conn = await OpenAsync(request, "postgres", ct);
                await ReadServerAsync(conn, p, ct);
            }
            catch (Exception inner)
            {
                p.Failed = true;
                p.Messages.Add(Describe(inner));
            }
        }
        catch (Exception ex)
        {
            p.Failed = true;
            p.Messages.Add(Describe(ex));
        }

        var ready = p.Connected && !p.Failed;
        if (p.Connected)
        {
            if (p.VersionNum < MinServerVersion)
            {
                ready = false;
                p.Messages.Add($"PostgreSQL 15 ou supérieur est requis (version détectée : {p.Version}).");
            }
            if (!p.IsSuperuser && !p.CanCreateRole)
            {
                ready = false;
                p.Messages.Add("L'utilisateur doit être superutilisateur ou avoir le droit CREATEROLE : l'installation crée le rôle applicatif (sans BYPASSRLS).");
            }
            if (!p.DatabaseExists && !p.IsSuperuser && !p.CanCreateDatabase)
            {
                ready = false;
                p.Messages.Add($"La base « {database} » n'existe pas et l'utilisateur ne peut pas la créer (droit CREATEDB).");
            }
            if (p.DatabaseExists && !p.IsSuperuser && !p.CanCreateInDatabase)
            {
                ready = false;
                p.Messages.Add($"L'utilisateur n'a pas le droit CREATE sur la base « {database} ».");
            }
            if (p.AlreadyInitialized)
            {
                ready = false;
                p.Messages.Add($"La base « {database} » contient déjà des données Nexora One : l'installation initiale est refusée. Choisissez une base vide.");
            }
            if (ready)
            {
                p.Messages.Add(p.DatabaseExists
                    ? $"Connexion réussie (PostgreSQL {p.Version}) — la base « {database} » est prête pour l'installation."
                    : $"Connexion réussie (PostgreSQL {p.Version}) — la base « {database} » sera créée.");
            }
        }

        return new DatabaseTestResultDto(p.Connected, p.Version, p.DatabaseExists, p.AlreadyInitialized,
            p.IsSuperuser, p.CanCreateRole, p.CanCreateDatabase, ready, p.Messages);
    }

    private static async Task ReadServerAsync(NpgsqlConnection conn, Probe p, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT current_setting('server_version'), current_setting('server_version_num')::int,
                   r.rolsuper, r.rolcreaterole, r.rolcreatedb
            FROM pg_roles r WHERE r.rolname = current_user
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            p.Version = reader.GetString(0);
            p.VersionNum = reader.GetInt32(1);
            p.IsSuperuser = reader.GetBoolean(2);
            p.CanCreateRole = reader.GetBoolean(3);
            p.CanCreateDatabase = reader.GetBoolean(4);
        }
        p.Connected = true;
    }

    private static async Task ReadDatabaseAsync(NpgsqlConnection conn, Probe p, CancellationToken ct)
    {
        p.CanCreateInDatabase = await ScalarAsync<bool>(conn, null,
            "SELECT has_database_privilege(current_database(), 'CREATE')", ct);
        if (!await ScalarAsync<bool>(conn, null, "SELECT to_regclass('nexora.company') IS NOT NULL", ct)) return;

        // FORCE ROW LEVEL SECURITY : le propriétaire lui-même ne voit rien sans la portée système.
        // Une requête par table : une base partiellement créée peut n'avoir que l'une des deux.
        await ExecAsync(conn, null, "SET nexora.bypass_rls = 'on'", ct);
        p.AlreadyInitialized = await ScalarAsync<bool>(conn, null, "SELECT EXISTS (SELECT 1 FROM nexora.company)", ct)
            || (await ScalarAsync<bool>(conn, null, "SELECT to_regclass('nexora.app_user') IS NOT NULL", ct)
                && await ScalarAsync<bool>(conn, null, "SELECT EXISTS (SELECT 1 FROM nexora.app_user)", ct));
    }

    // =====================================================================================
    // Installation
    // =====================================================================================
    public async Task<Result<InstallResultDto>> InstallAsync(InstallRequest request, CancellationToken ct = default)
    {
        if (!state.Required)
            return Error.NotFound("setup.disabled", "L'assistant d'installation est désactivé : l'application est déjà configurée.");
        if (state.Installed)
            return Error.Conflict("setup.completed", "L'installation est terminée : redémarrez le service.");
        if (ValidateConnection(request.Database) is { } badDb) return badDb;
        if (ValidateInstall(request) is { } invalid) return invalid;
        if (!state.TryBeginInstall())
            return Error.Conflict("setup.in_progress", "Une installation est déjà en cours.");

        var succeeded = false;
        try
        {
            if (CheckConfigWritable() is { } notWritable) return notWritable;

            var probe = await ProbeAsync(request.Database, ct);
            if (!probe.Ready)
                return Error.Validation("setup.database_not_ready", string.Join(" ", probe.Messages));

            var database = request.Database.Database.Trim();
            if (!probe.DatabaseExists)
            {
                await using var maintenance = await OpenAsync(request.Database, "postgres", ct);
                await ExecAsync(maintenance, null, $"CREATE DATABASE {QuoteIdent(database)} ENCODING 'UTF8' TEMPLATE template0", ct);
                logger.LogInformation("Installation : base {Database} créée.", database);
            }

            await using var conn = await OpenAsync(request.Database, database, ct);
            await ExecAsync(conn, null, "SET nexora.bypass_rls = 'on'", ct);

            // V020 crée « nexora_app » avec un mot de passe connu s'il n'existe pas. Créé par cette
            // installation, il devient un simple rôle de groupe (NOLOGIN) : l'application se connecte
            // par un rôle dédié, membre de nexora_app, au mot de passe généré.
            var appRoleExisted = await ScalarAsync<bool>(conn, null,
                "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_app')", ct);
            int applied;
            try
            {
                applied = await MigrationRunner.ApplyPendingAsync(conn, ct);
            }
            finally
            {
                if (!appRoleExisted && await ScalarAsync<bool>(conn, null,
                        "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_app')", CancellationToken.None))
                    await ExecAsync(conn, null, "ALTER ROLE nexora_app NOLOGIN PASSWORD NULL", CancellationToken.None);
            }
            logger.LogInformation("Installation : {Count} migration(s) appliquée(s).", applied);

            var outcome = await CreateTenantAsync(conn, request, ct);
            if (!outcome.IsSuccess) return outcome.Error!;
            var (companyId, roleName, rolePassword) = outcome.Value;

            await WriteConfigurationAsync(request.Database, database, roleName, rolePassword, companyId, ct);
            succeeded = true;
            logger.LogWarning("Installation terminée : entreprise {CompanyId}, administrateur {Email}. Redémarrez le service.",
                companyId, request.Admin.Email.Trim());

            return Result.Success(new InstallResultDto(companyId, request.Company.Name.Trim(), request.Admin.Email.Trim().ToLowerInvariant(),
                applied, RestartRequired: true));
        }
        finally
        {
            state.EndInstall(succeeded);
        }
    }

    /// <summary>
    /// Premier démarrage d'une installation par conteneurs (finalisation phase 18) : la base a été migrée par la commande
    /// « migrate » et l'application se connecte déjà avec le rôle applicatif fourni par l'orchestrateur. Crée l'entreprise et
    /// son premier administrateur — exactement comme l'assistant (mêmes validations, même transaction), sans rôle de
    /// connexion dédié ni fichier de configuration. Refusé si la base contient déjà une entreprise ou un utilisateur.
    /// </summary>
    public async Task<Result<Guid>> BootstrapAsync(NpgsqlConnection conn, SetupCompanyInput company, SetupAdminInput admin,
        SetupSettingsInput? settings = null, CancellationToken ct = default)
    {
        var request = new InstallRequest(new DatabaseSetupRequest("-", 0, "-", "-", "-"), company, admin, settings);
        if (ValidateInstall(request) is { } invalid) return invalid;
        if (!await ScalarAsync<bool>(conn, null, "SELECT to_regclass('nexora.company') IS NOT NULL", ct))
            return Error.Validation("setup.not_migrated", "La base n'est pas migrée : lancer « migrate » d'abord.");
        await ExecAsync(conn, null, "SET nexora.bypass_rls = 'on'", ct);
        var outcome = await CreateTenantAsync(conn, request, ct, createRuntimeRole: false);
        if (!outcome.IsSuccess) return outcome.Error!;
        logger.LogWarning("Premier démarrage : entreprise {CompanyId}, administrateur {Email}.", outcome.Value.CompanyId,
            admin.Email.Trim().ToLowerInvariant());
        return Result.Success(outcome.Value.CompanyId);
    }

    private async Task<Result<(Guid CompanyId, string RoleName, string RolePassword)>> CreateTenantAsync(
        NpgsqlConnection conn, InstallRequest request, CancellationToken ct, bool createRuntimeRole = true)
    {
        var company = request.Company;
        var admin = request.Admin;
        var settings = request.Settings ?? new SetupSettingsInput();
        var activity = company.ActivityType.Trim().ToLowerInvariant();
        var email = admin.Email.Trim().ToLowerInvariant();

        await using var tx = await conn.BeginTransactionAsync(ct);

        // Garde de course : une autre installation a pu remplir la base entre le test et maintenant.
        if (await ScalarAsync<bool>(conn, tx, "SELECT EXISTS (SELECT 1 FROM nexora.company) OR EXISTS (SELECT 1 FROM nexora.app_user)", ct))
            return Error.Conflict("setup.database_not_empty", "La base contient déjà des données Nexora One : installation refusée.");

        var currencyId = await ScalarAsync<Guid?>(conn, tx, "SELECT id FROM nexora.currency WHERE code = @p0", ct, company.Currency.Trim().ToUpperInvariant());
        if (currencyId is null)
            return Error.Validation("setup.currency", $"Devise « {company.Currency} » inconnue.");

        // ---- Entreprise + référentiel prêt à vendre (V072) ----------------------------------
        var companyId = Guid.NewGuid();
        await ExecAsync(conn, tx, """
            INSERT INTO nexora.company (id, name, main_activity_type, default_currency_id, complexity_level, deployment_mode)
            VALUES (@p0, @p1, @p2, @p3, 'STANDARD', 'HYBRID')
            """, ct, companyId, company.Name.Trim(), activity, currencyId.Value);
        // Année du premier exercice dans le fuseau de l'entreprise (audit 2026-10 éd. 2 : en UTC, une installation faite
        // le 31 décembre au soir à Port-au-Prince ouvrait l'exercice de l'année suivante).
        var zoneId = await ScalarAsync<string>(conn, tx, "SELECT timezone FROM nexora.company WHERE id = @p0", ct, companyId);
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(zoneId ?? "", out var found) ? found : TimeZoneInfo.Utc;
        var year = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Year;
        await ExecAsync(conn, tx, "INSERT INTO nexora.company_activity (company_id, activity_type, is_primary) VALUES (@p0, @p1, true)",
            ct, companyId, activity);
        await ExecAsync(conn, tx, """
            INSERT INTO nexora.company_module (company_id, module_key)
            SELECT @p0, module_key FROM nexora.sector_module_preset WHERE activity_type = @p1
            ON CONFLICT DO NOTHING
            """, ct, companyId, activity);
        await ExecAsync(conn, tx, "SELECT nexora.bootstrap_company_defaults(@p0, @p1, @p2)", ct, companyId, currencyId.Value, year);

        // ---- Paramètres (§20 « Configurer les paramètres ») ----------------------------------
        var values = new (string Key, string Json)[]
        {
            ("pos.require_cash_session", settings.RequireCashSession ? "true" : "false"),
            ("pos.cash_variance_threshold", settings.CashVarianceThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("pos.max_discount_percent", settings.MaxDiscountPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("stock.negative_stock_allowed", settings.AllowNegativeStock ? "true" : "false"),
        };
        foreach (var (key, json) in values)
            await ExecAsync(conn, tx, """
                INSERT INTO nexora.setting (key, scope, scope_id, company_id, value, note)
                VALUES (@p0, 'company', @p1, @p1, @p2::jsonb, 'Installation initiale')
                ON CONFLICT (key, scope, scope_id) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
                """, ct, key, companyId, json);

        // ---- Premier administrateur ------------------------------------------------------------
        var adminId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        await ExecAsync(conn, tx, """
            INSERT INTO nexora.app_user (id, email, full_name, password_hash, status)
            VALUES (@p0, @p1::citext, @p2, @p3, 'active')
            """, ct, adminId, email, admin.FullName.Trim(), hasher.Hash(admin.Password));
        await ExecAsync(conn, tx, "INSERT INTO nexora.access_scope (id, scope_type, company_id) VALUES (@p0, 'COMPANY', @p1)",
            ct, scopeId, companyId);
        var granted = await ExecAsync(conn, tx, """
            INSERT INTO nexora.user_role (user_id, role_id, scope_id, reason)
            SELECT @p0, r.id, @p1, 'Installation initiale' FROM nexora.role r
            WHERE r.name = @p2 AND r.company_id IS NULL
            """, ct, adminId, scopeId, InitialAdminRole);
        if (granted != 1)
            throw new InvalidOperationException($"Rôle système « {InitialAdminRole} » introuvable après migrations.");

        // ---- Rôle de connexion de l'application : membre de nexora_app, jamais BYPASSRLS ------------
        // (installation par conteneurs : l'orchestrateur fournit déjà la connexion du rôle applicatif)
        string roleName = "", rolePassword = "";
        if (createRuntimeRole)
        {
            roleName = "nexora_rt_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            rolePassword = RandomAlphanumeric(40);
            await ExecAsync(conn, tx,
                $"CREATE ROLE {roleName} LOGIN PASSWORD '{rolePassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT IN ROLE nexora_app", ct);
        }

        await ExecAsync(conn, tx, """
            INSERT INTO nexora.audit_log (company_id, user_id, action, entity_type, entity_id, new_values)
            VALUES (@p0, @p1, 'setup.installed', 'company', @p0, @p2::jsonb)
            """, ct, companyId, adminId, JsonSerializer.Serialize(new
            {
                company = company.Name.Trim(), activity, currency = company.Currency.Trim().ToUpperInvariant(),
                admin = email, runtime_role = createRuntimeRole ? roleName : "fourni par l'orchestrateur",
            }));

        await tx.CommitAsync(ct);
        return Result.Success((companyId, roleName, rolePassword));
    }

    // =====================================================================================
    // Configuration écrite (lue au redémarrage, au-dessus des appsettings)
    // =====================================================================================
    private Error? CheckConfigWritable()
    {
        try
        {
            var dir = Path.GetDirectoryName(state.ConfigPath)!;
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".nexora-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Validation("setup.config_not_writable",
                $"Impossible d'écrire la configuration dans « {Path.GetDirectoryName(state.ConfigPath)} ». " +
                "Définissez Install:ConfigPath vers un dossier accessible en écriture, puis relancez l'installation.");
        }
    }

    private async Task WriteConfigurationAsync(DatabaseSetupRequest db, string database, string roleName, string rolePassword,
        Guid companyId, CancellationToken ct)
    {
        var runtime = new NpgsqlConnectionStringBuilder
        {
            Host = db.Host.Trim(),
            Port = db.Port,
            Database = database,
            Username = roleName,
            Password = rolePassword,
            SslMode = ParseSsl(db.SslMode),
            SearchPath = "nexora,public",
        };
        var document = new Dictionary<string, object>
        {
            ["Database"] = new Dictionary<string, string> { ["ConnectionString"] = runtime.ConnectionString },
            ["Jwt"] = new Dictionary<string, string> { ["SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) },
            // Deux clés distinctes : secrets MFA d'un côté, données personnelles (paie, banque) de l'autre (audit 2026-10 éd. 2).
            ["Security"] = new Dictionary<string, string>
            {
                ["MfaEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["DataEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            },
            ["Install"] = new Dictionary<string, string>
            {
                ["CompletedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["CompanyId"] = companyId.ToString(),
                ["Database"] = database,
                ["RuntimeRole"] = roleName,
            },
        };

        var temp = state.ConfigPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }), ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, state.ConfigPath, overwrite: true);
    }

    // =====================================================================================
    // Validation & utilitaires
    // =====================================================================================
    private static Error? ValidateConnection(DatabaseSetupRequest? r)
    {
        if (r is null) return Error.Validation("setup.database", "Paramètres PostgreSQL requis.");
        if (string.IsNullOrWhiteSpace(r.Host)) return Error.Validation("setup.database_host", "Hôte PostgreSQL requis.");
        if (r.Port is < 1 or > 65535) return Error.Validation("setup.database_port", "Port PostgreSQL invalide.");
        if (string.IsNullOrWhiteSpace(r.Database) || !DatabaseName().IsMatch(r.Database.Trim()))
            return Error.Validation("setup.database_name", "Nom de base invalide (lettres, chiffres et « _ », 63 caractères au plus).");
        if (string.IsNullOrWhiteSpace(r.Username)) return Error.Validation("setup.database_user", "Utilisateur PostgreSQL requis.");
        if (!SslModes.Contains(r.SslMode ?? "Prefer", StringComparer.OrdinalIgnoreCase))
            return Error.Validation("setup.database_ssl", "Mode SSL invalide.");
        return null;
    }

    private static Error? ValidateInstall(InstallRequest r)
    {
        if (r.Company is null || string.IsNullOrWhiteSpace(r.Company.Name) || r.Company.Name.Trim().Length > 200)
            return Error.Validation("setup.company_name", "Nom de l'entreprise requis (200 caractères au plus).");
        if (!SqlCompanyProvisioningService.Activities.Contains((r.Company.ActivityType ?? "").Trim().ToLowerInvariant()))
            return Error.Validation("setup.company_activity", $"Activité « {r.Company.ActivityType} » inconnue.");
        if (!CurrencyChoices.Any(c => string.Equals(c.Code, r.Company.Currency?.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Error.Validation("setup.currency", $"Devise « {r.Company.Currency} » inconnue.");
        if (r.Admin is null || string.IsNullOrWhiteSpace(r.Admin.FullName))
            return Error.Validation("setup.admin_name", "Nom de l'administrateur requis.");
        if (string.IsNullOrWhiteSpace(r.Admin.Email) || !EmailShape().IsMatch(r.Admin.Email.Trim()))
            return Error.Validation("setup.admin_email", "Adresse e-mail de l'administrateur invalide.");
        if (PasswordPolicy.Validate(r.Admin.Password, r.Admin.Email.Trim()) is { } weak) return weak;
        if (r.Settings is { } s)
        {
            if (s.CashVarianceThreshold is < 0 or > 1_000_000_000)
                return Error.Validation("setup.cash_variance_threshold", "Seuil d'écart de caisse invalide.");
            if (s.MaxDiscountPercent is < 0 or > 100)
                return Error.Validation("setup.max_discount_percent", "La remise maximale doit être comprise entre 0 et 100 %.");
        }
        return null;
    }

    private static SslMode ParseSsl(string? mode)
        => Enum.TryParse<SslMode>(mode ?? "Prefer", ignoreCase: true, out var m) ? m : SslMode.Prefer;

    private static async Task<NpgsqlConnection> OpenAsync(DatabaseSetupRequest r, string database, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = r.Host.Trim(),
            Port = r.Port,
            Database = database,
            Username = r.Username.Trim(),
            Password = r.Password,
            SslMode = ParseSsl(r.SslMode),
            Timeout = 10,
            // CREATE DATABASE attend un checkpoint : sur un serveur chargé, bien plus que 30 s.
            CommandTimeout = 300,
            Pooling = false,   // aucune connexion d'installation ne doit survivre (DROP/CREATE DATABASE)
            ApplicationName = "Nexora One - installation",
        };
        var conn = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"p{i}", args[i]);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"p{i}", args[i]);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default! : (T)value;
    }

    private static string QuoteIdent(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string RandomAlphanumeric(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(length, alphabet, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
    }

    private static string Describe(Exception ex) => ex switch
    {
        PostgresException { SqlState: PostgresErrorCodes.InvalidPassword } =>
            "Authentification refusée : utilisateur ou mot de passe incorrect.",
        PostgresException { SqlState: PostgresErrorCodes.InvalidAuthorizationSpecification } pe =>
            $"Connexion refusée par le serveur (pg_hba.conf) : {pe.MessageText}",
        PostgresException pe => $"Erreur PostgreSQL : {pe.MessageText}",
        NpgsqlException { InnerException: SocketException or TimeoutException } or TimeoutException =>
            "Serveur PostgreSQL injoignable : vérifiez l'hôte, le port et le pare-feu.",
        NpgsqlException ne when ne.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) =>
            $"Échec de la négociation SSL : {ne.Message}",
        _ => $"Connexion impossible : {ex.Message}",
    };
}
