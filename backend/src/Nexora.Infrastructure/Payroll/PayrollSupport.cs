using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Notifications;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Payroll;

/// <summary>Règles et lectures partagées par les services de paie.</summary>
public static class PayrollRules
{
    public static readonly IReadOnlySet<string> PaymentMethods = new HashSet<string>(["bank_transfer", "cash", "mobile_money", "check", "other"]);
    public static readonly IReadOnlySet<string> ContractTypes = new HashSet<string>(
        ["permanent", "fixed_term", "temporary", "internship", "apprenticeship", "consultant", "daily"]);
    public static readonly IReadOnlySet<string> Frequencies = new HashSet<string>(["monthly", "biweekly", "weekly"]);
    public static readonly IReadOnlySet<string> PayBases = new HashSet<string>(["monthly", "daily", "hourly"]);
    public static readonly IReadOnlySet<string> LeaveTypes = new HashSet<string>(
        ["annual", "sick", "maternity", "paternity", "unpaid", "justified_absence", "unjustified_absence", "other"]);
    public static readonly IReadOnlySet<string> PaidLeaveTypes = new HashSet<string>(["annual", "sick", "maternity", "paternity", "justified_absence"]);
    public static readonly IReadOnlySet<string> AttendanceStatuses = new HashSet<string>(
        ["present", "absent", "late", "half_day", "holiday", "rest", "leave"]);
    public static readonly IReadOnlySet<string> OvertimeTypes = new HashSet<string>(["standard", "night", "weekend", "holiday"]);
    public static readonly IReadOnlySet<string> AdvanceKinds = new HashSet<string>(["salary_advance", "acompte", "loan"]);
    public static readonly IReadOnlySet<string> RecoveryMethods = new HashSet<string>(["next_payroll", "installments", "percent_of_net", "manual"]);
    public static readonly IReadOnlySet<string> ExpenseCategories = new HashSet<string>(
        ["transport", "meal", "lodging", "supplies", "communication", "fuel", "other"]);
    public static readonly IReadOnlySet<string> DocumentTypes = new HashSet<string>(
        ["contract", "id_document", "diploma", "certificate", "medical", "expense_receipt", "disciplinary", "other"]);

    public static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static Guid Company(ITenantContext tenant)
        => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    public static Guid User(ITenantContext tenant)
        => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    /// <summary>Numéro de pièce : préfixe + séquence d'entreprise par an (ex. <c>AVS-2026-000001</c>).</summary>
    public static async Task<string> NextNumberAsync(NexoraDbContext db, Guid company, string type, string prefix, int? year, CancellationToken ct)
    {
        var y = year;   // null : année locale de l'entreprise (current_date, session au fuseau de l'entreprise)
        var rows = await db.Database.SqlQuery<string>(
            $"""SELECT nexora.next_document_number({company}, {type}, {(Guid?)null}, {y}) AS "Value" """).ToListAsync(ct);
        return prefix + rows[0];
    }

    public static string Masked(string? last4) => string.IsNullOrEmpty(last4) ? "" : "•••• " + last4;

    public static string? Last4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return digits.Length <= 4 ? digits : digits[^4..];
    }

    public static Task<DateOnly> TodayAsync(NexoraDbContext db, CancellationToken ct) => Reporting.ReportingClock.TodayAsync(db, ct);

    /// <summary>Opération dérivée déterministe (paiement groupé : une instruction par ligne, rejouable).</summary>
    public static Guid Derive(Guid operation, Guid item)
    {
        Span<byte> buffer = stackalloc byte[32];
        operation.TryWriteBytes(buffer[..16]);
        item.TryWriteBytes(buffer[16..]);
        var hash = SHA256.HashData(buffer);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>Refus métier levé dans une transaction pour l'annuler entièrement.</summary>
internal sealed class PayrollRefusal(Error error) : Exception(error.Message)
{
    public Error Error { get; } = error;
}

/// <summary>Paramètres de paie résolus (V076) — valeur de l'entreprise, sinon défaut du catalogue.</summary>
public sealed record PayrollConfiguration(
    PayrollSettings Calc,
    AdvancePolicySettings Advances,
    IReadOnlyDictionary<string, decimal> OvertimeCoefficients,
    bool SegregationOfDuties,
    bool ManagerTeamOnly,
    decimal AnnualLeaveDays,
    int AdvanceDueNoticeDays,
    int FrequentAdvanceCount,
    string PayslipLegalMentions);

public sealed class PayrollConfigurationReader(ISettingsService settings, ITenantContext tenant, ILogger<PayrollConfigurationReader> logger)
{
    private PayrollConfiguration? _cached;
    private Guid? _cachedCompany;

    public async Task<PayrollConfiguration> ReadAsync(CancellationToken ct = default)
    {
        if (_cached is not null && _cachedCompany == tenant.ActiveCompanyId) return _cached;
        _cachedCompany = tenant.ActiveCompanyId;
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            foreach (var s in await settings.EffectiveAsync(null, ct))
                if (s.Key.StartsWith("payroll.", StringComparison.Ordinal)) values[s.Key] = s.Value;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Paramètres de paie illisibles : valeurs par défaut appliquées");
        }

        decimal Num(string key, decimal fallback) =>
            values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal()
            : values.TryGetValue(key, out v) && v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
                System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d
            : fallback;
        bool Bool(string key, bool fallback) =>
            values.TryGetValue(key, out var v) ? v.ValueKind switch
            {
                JsonValueKind.True => true, JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback, _ => fallback,
            } : fallback;
        string Str(string key, string fallback) =>
            values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

        var weekDays = new HashSet<int>();
        if (values.TryGetValue("payroll.working_week_days", out var wd) && wd.ValueKind == JsonValueKind.Array)
            foreach (var x in wd.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Number && x.GetInt32() is var n and >= 1 and <= 7) weekDays.Add(n);
        if (weekDays.Count == 0) weekDays = [1, 2, 3, 4, 5, 6];

        var coefficients = new Dictionary<string, decimal> { ["standard"] = 1.5m, ["night"] = 1.5m, ["weekend"] = 2m, ["holiday"] = 2m };
        if (values.TryGetValue("payroll.overtime_coefficients", out var oc) && oc.ValueKind == JsonValueKind.Object)
            foreach (var p in oc.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number) coefficients[p.Name] = p.Value.GetDecimal();

        var basis = Str("payroll.day_count_basis", "calendar");
        _cached = new PayrollConfiguration(
            new PayrollSettings(
                basis == "working" ? "working" : "calendar",
                Math.Max(1m, Num("payroll.days_per_month", 30m)),
                weekDays,
                Math.Max(1m, Num("payroll.default_weekly_hours", 48m)),
                Math.Clamp(Num("payroll.max_recovery_percent_of_net", 100m), 0m, 100m),
                Math.Max(0m, Num("payroll.min_net_amount", 0m)),
                Num("payroll.variance_alert_percent", 20m),
                Num("payroll.overtime_alert_hours", 40m)),
            new AdvancePolicySettings(
                Num("payroll.advance_max_percent_of_salary", 50m),
                Bool("payroll.allow_multiple_advances", true),
                (int)Num("payroll.max_open_advances", 3m),
                Num("payroll.advance_ceiling_percent", 100m),
                Num("payroll.loan_ceiling_percent", 300m),
                Num("payroll.debt_near_limit_percent", 80m)),
            coefficients,
            Bool("payroll.segregation_of_duties", true),
            Bool("payroll.manager_team_only", true),
            Num("payroll.annual_leave_days", 15m),
            (int)Num("payroll.advance_due_notice_days", 5m),
            (int)Num("payroll.frequent_advance_count", 3m),
            Str("payroll.payslip_legal_mentions", ""));
        return _cached;
    }
}

/// <summary>Périmètre d'employés visible : tous, une liste (équipe et/ou soi-même), ou aucun.</summary>
public sealed record EmployeeScope(bool All, IReadOnlySet<Guid> Ids)
{
    public static EmployeeScope None { get; } = new(false, new HashSet<Guid>());
    public static EmployeeScope Everyone { get; } = new(true, new HashSet<Guid>());
    public bool Allows(Guid employeeId) => All || Ids.Contains(employeeId);
    public bool IsEmpty => !All && Ids.Count == 0;
}

/// <summary>
/// Contrôle d'accès RH côté serveur (§5 : « cacher un menu dans React ne constitue pas une sécurité ») :
/// permissions de l'entreprise active, dossier propre de l'utilisateur (espace employé), équipe d'un Manager.
/// </summary>
public sealed class PayrollAccess(NexoraDbContext db, ITenantContext tenant, IPermissionService permissions, PayrollConfigurationReader config)
{
    private IReadOnlyCollection<string>? _perms;
    private Guid? _ownEmployee;
    private bool _ownLoaded;
    private HashSet<Guid>? _team;

    /// <summary>Droits « à l'échelle de l'entreprise » : un utilisateur qui n'en a aucun et ne fait que valider
    /// des demandes est un Manager, restreint à son équipe si le paramètre le prévoit.</summary>
    private static readonly string[] CompanyWide =
    [
        Permissions.Hr.ManageEmployees, Permissions.Hr.ManageTime, Permissions.Payroll.ViewSalaries,
        Permissions.Payroll.Calculate, Permissions.Payroll.Reports, Permissions.Payroll.Audit,
    ];

    public Guid Company => PayrollRules.Company(tenant);
    public Guid? UserId => tenant.UserId;

    public async Task<IReadOnlyCollection<string>> PermissionsAsync(CancellationToken ct = default)
        => _perms ??= await permissions.CurrentPermissionsAsync(tenant, ct);

    public async Task<bool> HasAsync(string permission, CancellationToken ct = default)
        => (await PermissionsAsync(ct)).Contains(permission);

    public async Task<bool> HasAnyAsync(IEnumerable<string> codes, CancellationToken ct = default)
    {
        var perms = await PermissionsAsync(ct);
        return codes.Any(perms.Contains);
    }

    public async Task<bool> CanViewSalariesAsync(CancellationToken ct = default) => await HasAsync(Permissions.Payroll.ViewSalaries, ct);

    public async Task<Guid?> OwnEmployeeIdAsync(CancellationToken ct = default)
    {
        if (_ownLoaded) return _ownEmployee;
        _ownLoaded = true;
        if (tenant.UserId is not { } user || tenant.ActiveCompanyId is not { } company) return null;
        _ownEmployee = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.employee WHERE company_id = {company} AND user_id = {user}
            """).FirstOrDefaultAsync(ct);
        return _ownEmployee;
    }

    public async Task<bool> IsTeamRestrictedAsync(CancellationToken ct = default)
        => !await HasAnyAsync(CompanyWide, ct) && (await config.ReadAsync(ct)).ManagerTeamOnly;

    public async Task<IReadOnlySet<Guid>> TeamAsync(CancellationToken ct = default)
    {
        if (_team is not null) return _team;
        if (tenant.UserId is not { } user) return _team = [];
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT t AS "Value" FROM nexora.employee_team({Company}, {user}) AS t
            """).ToListAsync(ct);
        return _team = ids.ToHashSet();
    }

    /// <summary>
    /// Périmètre pour une lecture : <paramref name="wide"/> donne tout (sauf Manager restreint à son équipe),
    /// <paramref name="team"/> donne l'équipe (ou tout si la restriction est désactivée), l'espace employé donne soi.
    /// </summary>
    public async Task<EmployeeScope> ScopeAsync(string[] wide, string[] team, bool allowSelf, CancellationToken ct = default)
    {
        var restricted = await IsTeamRestrictedAsync(ct);
        if (await HasAnyAsync(wide, ct) && !restricted) return EmployeeScope.Everyone;
        var ids = new HashSet<Guid>();
        if (await HasAnyAsync(wide, ct) || await HasAnyAsync(team, ct))
        {
            if (!restricted) return EmployeeScope.Everyone;
            ids.UnionWith(await TeamAsync(ct));
        }
        if (allowSelf && await HasAsync(Permissions.Payroll.SelfService, ct) && await OwnEmployeeIdAsync(ct) is { } own)
            ids.Add(own);
        return ids.Count == 0 ? EmployeeScope.None : new EmployeeScope(false, ids);
    }

    /// <summary>Décider une demande : droit requis, jamais pour soi-même, et dans l'équipe pour un Manager restreint.</summary>
    public async Task<Error?> CanDecideAsync(string permission, Guid employeeId, Guid? requestedBy, CancellationToken ct = default)
    {
        if (!await HasAsync(permission, ct))
            return Error.Forbidden("payroll.forbidden", "Vous n'avez pas le droit de valider cette demande.");
        if (requestedBy is { } r && r == tenant.UserId)
            return Error.Forbidden("payroll.self_approval", "Vous ne pouvez pas valider votre propre demande (séparation des tâches).");
        if (await OwnEmployeeIdAsync(ct) is { } own && own == employeeId)
            return Error.Forbidden("payroll.self_approval", "Vous ne pouvez pas valider une demande qui vous concerne.");
        if (await IsTeamRestrictedAsync(ct) && !(await TeamAsync(ct)).Contains(employeeId))
            return Error.Forbidden("payroll.not_in_team", "Cet employé n'appartient pas à votre équipe.");
        return null;
    }

    /// <summary>Saisie pour un employé : droit « large » ou, dans l'espace employé, pour soi-même seulement.</summary>
    public async Task<Error?> CanActForAsync(string widePermission, Guid employeeId, CancellationToken ct = default)
    {
        if (await HasAsync(widePermission, ct)) return null;
        if (await HasAsync(Permissions.Payroll.SelfService, ct) && await OwnEmployeeIdAsync(ct) is { } own && own == employeeId) return null;
        return Error.Forbidden("payroll.forbidden", "Action non autorisée pour cet employé.");
    }

    /// <summary>Journalise la lecture d'une donnée sensible (§35 « accès ou export de données sensibles »).</summary>
    public async Task LogSensitiveAccessAsync(string entityType, Guid entityId, string what, CancellationToken ct = default)
    {
        var context = JsonSerializer.Serialize(new { access = what, at = DateTimeOffset.UtcNow });
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.audit_log (company_id, user_id, action, entity_type, entity_id, context)
            VALUES ({Company}, {tenant.UserId}, 'READ_SENSITIVE', {entityType}, {entityId}, {context}::jsonb)
            """, ct);
    }
}

/// <summary>Chiffrement AES-256-GCM des identifiants bancaires et fiscaux (§7 « protégées »). Clé
/// <c>Security:DataEncryptionKey</c> (base64, 32 octets), sinon la clé MFA, sinon une clé de développement.</summary>
public sealed class PersonalDataProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public PersonalDataProtector(IConfiguration configuration)
    {
        var configured = configuration["Security:DataEncryptionKey"];
        if (string.IsNullOrWhiteSpace(configured)) configured = configuration["Security:MfaEncryptionKey"];
        _key = string.IsNullOrWhiteSpace(configured)
            ? SHA256.HashData("dev-only-insecure-personal-data-key-change-me"u8.ToArray())
            : Convert.FromBase64String(configured);
        if (_key.Length != 32)
            throw new InvalidOperationException("Security:DataEncryptionKey doit décoder en 32 octets (AES-256).");
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = System.Text.Encoding.UTF8.GetBytes(plaintext.Trim());
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        var packed = Convert.FromBase64String(ciphertext);
        var plain = new byte[packed.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(packed[..NonceSize], packed[(NonceSize + TagSize)..], packed[NonceSize..(NonceSize + TagSize)], plain);
        return System.Text.Encoding.UTF8.GetString(plain);
    }
}

/// <summary>
/// Notifications de paie (§34). Un échec de notification ne remet jamais en cause l'opération enregistrée.
/// </summary>
public sealed class PayrollNotifier(
    NexoraDbContext db, ITenantContext tenant, INotificationService notifications, ILogger<PayrollNotifier> logger)
{
    /// <summary>Utilisateurs actifs détenant <paramref name="permission"/> dans l'entreprise active (hors l'appelant).</summary>
    public async Task<List<Guid>> HoldersAsync(string permission, CancellationToken ct)
    {
        var company = PayrollRules.Company(tenant);
        using (tenant.BeginSystemScope())
            return await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT ur.user_id AS "Value"
                FROM nexora.user_role ur
                JOIN nexora.role_permission rp ON rp.role_id = ur.role_id
                JOIN nexora.permission p ON p.id = rp.permission_id
                JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                JOIN nexora.app_user u ON u.id = ur.user_id AND u.status = 'active'
                WHERE p.code = {permission}
                  AND ur.revoked_at IS NULL
                  AND (ur.valid_from IS NULL OR ur.valid_from <= now())
                  AND (ur.valid_until IS NULL OR ur.valid_until > now())
                  AND (sc.company_id = {company}
                       OR (sc.group_id IS NOT NULL AND sc.group_id = (SELECT group_id FROM nexora.company WHERE id = {company})))
                  AND ({tenant.UserId}::uuid IS NULL OR ur.user_id <> {tenant.UserId}::uuid)
                LIMIT 50
                """).ToListAsync(ct);
    }

    public async Task<Guid?> EmployeeUserAsync(Guid employeeId, CancellationToken ct)
        => await db.Database.SqlQuery<Guid?>($"""
            SELECT user_id AS "Value" FROM nexora.employee WHERE id = {employeeId} AND company_id = {PayrollRules.Company(tenant)}
            """).FirstOrDefaultAsync(ct);

    public async Task ToPermissionAsync(string permission, string title, string detail, string actionUrl, string referenceType,
        Guid referenceId, string dedup, CancellationToken ct, string severity = "info")
    {
        try
        {
            var recipients = await HoldersAsync(permission, ct);
            await SendAsync(recipients, title, detail, actionUrl, referenceType, referenceId, dedup, severity, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification de paie non émise ({Dedup})", dedup);
        }
    }

    public async Task ToUsersAsync(IEnumerable<Guid?> users, string title, string detail, string actionUrl, string referenceType,
        Guid referenceId, string dedup, CancellationToken ct, string severity = "info")
    {
        try
        {
            var recipients = users.Where(u => u is not null && u != tenant.UserId).Select(u => u!.Value).Distinct().ToList();
            await SendAsync(recipients, title, detail, actionUrl, referenceType, referenceId, dedup, severity, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification de paie non émise ({Dedup})", dedup);
        }
    }

    private async Task SendAsync(List<Guid> recipients, string title, string detail, string actionUrl, string referenceType,
        Guid referenceId, string dedup, string severity, CancellationToken ct)
    {
        if (recipients.Count == 0) return;
        await notifications.NotifyAsync(new NotifyRequest(
            Category: "payroll",
            Recipients: recipients,
            TemplateKey: "payroll.event",
            Variables: new Dictionary<string, string?> { ["event.title"] = title, ["event.detail"] = detail },
            Priority: severity == "warning" ? 2 : 4, Severity: severity, Source: "workflow", ActionUrl: actionUrl,
            ReferenceType: referenceType, ReferenceId: referenceId, DedupKey: dedup), ct);
    }
}
