using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Payroll;
using Nexora.Contracts.Payroll;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.Infrastructure.Payroll;

/// <summary>
/// Composants de paie et règles légales (§11, §20, §31) : rien n'est codé en dur. Une règle naît en brouillon,
/// est validée par le responsable compétent (autre que son auteur si la séparation des tâches est active), puis
/// figée ; toute évolution crée une nouvelle version qui prend le relais à sa date d'effet.
/// </summary>
public sealed class SqlPayrollRuleService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    PayrollConfigurationReader config) : IPayrollRuleService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Guid Company => PayrollRules.Company(tenant);
    private Guid UserId => PayrollRules.User(tenant);

    // ================================================================== Composants

    private sealed record ComponentRow(
        Guid Id, bool IsSystem, string Code, string Name, string Kind, string Category, bool Taxable, bool SubjectToSocial, bool InGross,
        bool Cash, bool RequiresLegalBasis, decimal? DefaultAmount, string Status);

    private IQueryable<ComponentRow> ComponentQuery() => db.Database.SqlQuery<ComponentRow>($"""
        SELECT id AS "Id", (company_id IS NULL) AS "IsSystem", code AS "Code", name AS "Name", kind AS "Kind", category AS "Category",
               taxable AS "Taxable", subject_to_social AS "SubjectToSocial", in_gross AS "InGross", cash AS "Cash",
               requires_legal_basis AS "RequiresLegalBasis", default_amount AS "DefaultAmount", status AS "Status"
        FROM nexora.pay_component
        WHERE company_id = {Company} OR company_id IS NULL
        """);

    private static PayComponentDto ToDto(ComponentRow r) => new(r.Id, r.IsSystem, r.Code, r.Name, r.Kind, r.Category, r.Taxable,
        r.SubjectToSocial, r.InGross, r.Cash, r.RequiresLegalBasis, r.DefaultAmount, r.Status);

    public async Task<IReadOnlyList<PayComponentDto>> ComponentsAsync(CancellationToken ct = default)
        => (await ComponentQuery().OrderBy(c => c.Kind).ThenBy(c => c.Category).ThenBy(c => c.Code).ToListAsync(ct)).Select(ToDto).ToList();

    private static readonly IReadOnlySet<string> EarningCategories = new HashSet<string>(
        ["bonus", "commission", "allowance", "benefit_in_kind"]);
    private static readonly IReadOnlySet<string> DeductionCategories = new HashSet<string>(["other_deduction"]);

    public async Task<Result<PayComponentDto>> SaveComponentAsync(Guid? id, SavePayComponentRequest r, CancellationToken ct = default)
    {
        var code = r.Code?.Trim().ToUpperInvariant() ?? "";
        if (code.Length is 0 or > 40 || string.IsNullOrWhiteSpace(r.Name))
            return Error.Validation("payroll.component_invalid", "Code (40 caractères au plus) et libellé obligatoires.");
        // Les catégories calculées par le moteur (salaire, heures sup., absences, avances, frais, impôts…) restent système.
        var allowed = r.Kind == "earning" ? EarningCategories : r.Kind == "deduction" ? DeductionCategories : null;
        if (allowed is null || !allowed.Contains(r.Category))
            return Error.Validation("payroll.component_invalid",
                "Composant d'entreprise : gain (prime, commission, indemnité, avantage en nature) ou retenue « autre retenue documentée ».");
        if (r.Category == "benefit_in_kind" && r.Cash)
            return Error.Validation("payroll.component_invalid", "Un avantage en nature n'est pas versé en espèces.");
        var status = r.Status == "inactive" ? "inactive" : "active";
        try
        {
            if (id is { } existing)
            {
                var n = await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.pay_component SET code = {code}, name = {r.Name.Trim()}, kind = {r.Kind}, category = {r.Category},
                           taxable = {r.Taxable}, subject_to_social = {r.SubjectToSocial}, in_gross = {r.InGross}, cash = {r.Cash},
                           requires_legal_basis = {r.RequiresLegalBasis}, default_amount = {r.DefaultAmount}, status = {status}
                    WHERE id = {existing} AND company_id = {Company}
                    """, ct);
                if (n == 0)
                    return Error.Forbidden("payroll.component_system", "Composant système ou introuvable : créer un composant propre à l'entreprise.");
            }
            else
            {
                id = Guid.NewGuid();
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.pay_component
                        (id, company_id, code, name, kind, category, taxable, subject_to_social, in_gross, cash, requires_legal_basis, default_amount, status)
                    VALUES ({id}, {Company}, {code}, {r.Name.Trim()}, {r.Kind}, {r.Category}, {r.Taxable}, {r.SubjectToSocial}, {r.InGross},
                            {r.Cash}, {r.RequiresLegalBasis}, {r.DefaultAmount}, {status})
                    """, ct);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Error.Conflict("payroll.component_duplicate", $"Le code « {code} » existe déjà.");
        }
        return Result.Success(ToDto(await ComponentQuery().Where(c => c.Id == id).FirstAsync(ct)));
    }

    // ================================================================== Règles légales

    private sealed record RuleRow(
        Guid Id, bool IsTemplate, string Code, string Name, string Jurisdiction, string RuleType, string BaseType, string Method,
        decimal? Rate, decimal? FixedAmount, string Brackets, bool Annualize, decimal? BaseFloor, decimal? BaseCeiling, decimal? MinAmount,
        decimal? MaxAmount, DateOnly ValidFrom, DateOnly? ValidTo, int Version, string Status, string? LegalReference, string? Notes,
        Guid? SupersedesId, Guid? CreatedBy, string? ValidatedByName, DateTimeOffset? ValidatedAt, string? ValidationNote);

    private IQueryable<RuleRow> RuleQuery() => db.Database.SqlQuery<RuleRow>($"""
        SELECT r.id AS "Id", (r.company_id IS NULL) AS "IsTemplate", r.code AS "Code", r.name AS "Name", r.jurisdiction AS "Jurisdiction",
               r.rule_type AS "RuleType", r.base_type AS "BaseType", r.method AS "Method", r.rate AS "Rate", r.fixed_amount AS "FixedAmount",
               r.brackets::text AS "Brackets", r.annualize AS "Annualize", r.base_floor AS "BaseFloor", r.base_ceiling AS "BaseCeiling",
               r.min_amount AS "MinAmount", r.max_amount AS "MaxAmount", r.valid_from AS "ValidFrom", r.valid_to AS "ValidTo",
               r.version AS "Version", r.status AS "Status", r.legal_reference AS "LegalReference", r.notes AS "Notes",
               r.supersedes_id AS "SupersedesId", r.created_by AS "CreatedBy", u.full_name AS "ValidatedByName",
               r.validated_at AS "ValidatedAt", r.validation_note AS "ValidationNote"
        FROM nexora.payroll_rule r
        LEFT JOIN nexora.app_user u ON u.id = r.validated_by
        WHERE r.company_id = {Company} OR r.company_id IS NULL
        """);

    public static IReadOnlyList<RuleBracketDto> ParseBrackets(string json)
        => JsonSerializer.Deserialize<List<RuleBracketDto>>(json, Json) ?? [];

    private static PayrollRuleDto ToDto(RuleRow r) => new(r.Id, r.IsTemplate, r.Code, r.Name, r.Jurisdiction, r.RuleType, r.BaseType, r.Method,
        r.Rate, r.FixedAmount, ParseBrackets(r.Brackets), r.Annualize, r.BaseFloor, r.BaseCeiling, r.MinAmount, r.MaxAmount, r.ValidFrom,
        r.ValidTo, r.Version, r.Status, r.LegalReference, r.Notes, r.SupersedesId, r.ValidatedByName, r.ValidatedAt, r.ValidationNote);

    public async Task<IReadOnlyList<PayrollRuleDto>> RulesAsync(bool includeTemplates, CancellationToken ct = default)
    {
        var rows = await RuleQuery().OrderBy(r => r.Code).ThenByDescending(r => r.Version).ToListAsync(ct);
        return rows.Where(r => includeTemplates || !r.IsTemplate).Select(ToDto).ToList();
    }

    private static Error? Validate(SavePayrollRuleRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name))
            return Error.Validation("payroll.rule_invalid", "Code et libellé de la règle obligatoires.");
        if (r.RuleType is not ("employee_contribution" or "employer_contribution" or "income_tax"))
            return Error.Validation("payroll.rule_invalid", $"Type de règle « {r.RuleType} » inconnu.");
        if (r.BaseType is not ("taxable" or "social" or "gross" or "taxable_after_social"))
            return Error.Validation("payroll.rule_invalid", $"Assiette « {r.BaseType} » inconnue.");
        switch (r.Method)
        {
            case "percentage" when r.Rate is not (>= 0m and <= 100m):
                return Error.Validation("payroll.rule_invalid", "Taux compris entre 0 et 100 % obligatoire.");
            case "fixed" when r.FixedAmount is not >= 0m:
                return Error.Validation("payroll.rule_invalid", "Montant fixe positif obligatoire.");
            case "progressive":
            {
                var b = r.Brackets ?? [];
                if (b.Count == 0) return Error.Validation("payroll.rule_invalid", "Barème progressif : au moins une tranche.");
                var ordered = b.OrderBy(x => x.From).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    if (ordered[i].Rate is < 0m or > 100m) return Error.Validation("payroll.rule_invalid", "Taux de tranche entre 0 et 100 %.");
                    if (ordered[i].To is { } to && to <= ordered[i].From) return Error.Validation("payroll.rule_invalid", "Tranche vide ou inversée.");
                    if (i > 0 && ordered[i - 1].To != ordered[i].From)
                        return Error.Validation("payroll.rule_invalid", "Les tranches doivent se suivre sans trou ni chevauchement.");
                    if (i < ordered.Count - 1 && ordered[i].To is null)
                        return Error.Validation("payroll.rule_invalid", "Seule la dernière tranche peut être ouverte.");
                }
                break;
            }
            case "percentage" or "fixed":
                break;
            default:
                return Error.Validation("payroll.rule_invalid", $"Méthode « {r.Method} » inconnue.");
        }
        if (r.ValidTo is { } vt && vt < r.ValidFrom) return Error.Validation("payroll.rule_invalid", "Fin de validité antérieure au début.");
        return null;
    }

    private async Task<Guid> InsertAsync(SavePayrollRuleRequest r, int version, Guid? supersedes, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var brackets = JsonSerializer.Serialize(r.Method == "progressive" ? r.Brackets ?? [] : [], Json);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.payroll_rule
                (id, company_id, code, name, jurisdiction, rule_type, base_type, method, rate, fixed_amount, brackets, annualize,
                 base_floor, base_ceiling, min_amount, max_amount, valid_from, valid_to, version, status, legal_reference, notes,
                 supersedes_id, created_by)
            VALUES ({id}, {Company}, {r.Code.Trim().ToUpperInvariant()}, {r.Name.Trim()}, {r.Jurisdiction ?? "HT"}, {r.RuleType}, {r.BaseType},
                    {r.Method}, {(r.Method == "percentage" ? r.Rate : null)}, {(r.Method == "fixed" ? r.FixedAmount : null)}, {brackets}::jsonb,
                    {r.Annualize && r.Method == "progressive"}, {r.BaseFloor}, {r.BaseCeiling}, {r.MinAmount}, {r.MaxAmount}, {r.ValidFrom},
                    {r.ValidTo}, {version}, 'draft', {r.LegalReference}, {r.Notes}, {supersedes}, {UserId})
            """, ct);
        return id;
    }

    public async Task<Result<PayrollRuleDto>> CreateRuleAsync(SavePayrollRuleRequest r, CancellationToken ct = default)
    {
        if (Validate(r) is { } invalid) return invalid;
        var exists = await RuleQuery().AnyAsync(x => !x.IsTemplate && x.Code == r.Code.Trim().ToUpperInvariant(), ct);
        if (exists) return Error.Conflict("payroll.rule_exists", "Une règle porte déjà ce code : créer une nouvelle version.");
        var id = await InsertAsync(r, 1, null, ct);
        return Result.Success(ToDto(await RuleQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    public async Task<Result<PayrollRuleDto>> UpdateDraftAsync(Guid ruleId, SavePayrollRuleRequest r, CancellationToken ct = default)
    {
        if (Validate(r) is { } invalid) return invalid;
        var rule = await RuleQuery().Where(x => x.Id == ruleId && !x.IsTemplate).FirstOrDefaultAsync(ct);
        if (rule is null) return Error.NotFound("payroll.rule_not_found", "Règle introuvable.");
        if (rule.Status != "draft") return Error.Conflict("payroll.rule_frozen", "Règle validée : créer une nouvelle version.");
        var brackets = JsonSerializer.Serialize(r.Method == "progressive" ? r.Brackets ?? [] : [], Json);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payroll_rule SET name = {r.Name.Trim()}, jurisdiction = {r.Jurisdiction ?? "HT"}, rule_type = {r.RuleType},
                   base_type = {r.BaseType}, method = {r.Method}, rate = {(r.Method == "percentage" ? r.Rate : null)},
                   fixed_amount = {(r.Method == "fixed" ? r.FixedAmount : null)}, brackets = {brackets}::jsonb,
                   annualize = {r.Annualize && r.Method == "progressive"}, base_floor = {r.BaseFloor}, base_ceiling = {r.BaseCeiling},
                   min_amount = {r.MinAmount}, max_amount = {r.MaxAmount}, valid_from = {r.ValidFrom}, valid_to = {r.ValidTo},
                   legal_reference = {r.LegalReference}, notes = {r.Notes}
            WHERE id = {ruleId} AND company_id = {Company} AND status = 'draft'
            """, ct);
        return Result.Success(ToDto(await RuleQuery().Where(x => x.Id == ruleId).FirstAsync(ct)));
    }

    public async Task<Result<PayrollRuleDto>> NewVersionAsync(Guid ruleId, SavePayrollRuleRequest r, CancellationToken ct = default)
    {
        if (Validate(r) is { } invalid) return invalid;
        var rule = await RuleQuery().Where(x => x.Id == ruleId && !x.IsTemplate).FirstOrDefaultAsync(ct);
        if (rule is null) return Error.NotFound("payroll.rule_not_found", "Règle introuvable.");
        if (rule.Status == "draft") return Error.Conflict("payroll.rule_draft", "Cette règle est encore un brouillon : la modifier directement.");
        if (r.ValidFrom <= rule.ValidFrom)
            return Error.Validation("payroll.rule_invalid", "La nouvelle version doit prendre effet après le début de la version en cours.");
        var drafts = await RuleQuery().AnyAsync(x => !x.IsTemplate && x.Code == rule.Code && x.Status == "draft", ct);
        if (drafts) return Error.Conflict("payroll.rule_draft", "Une nouvelle version est déjà en brouillon pour cette règle.");
        var next = await RuleQuery().Where(x => !x.IsTemplate && x.Code == rule.Code).MaxAsync(x => x.Version, ct) + 1;
        var id = await InsertAsync(r with { Code = rule.Code }, next, rule.Id, ct);
        return Result.Success(ToDto(await RuleQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    public async Task<Result<PayrollRuleDto>> ValidateAsync(Guid ruleId, ValidateRuleRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Note) || r.Note.Trim().Length < 5)
            return Error.Validation("payroll.rule_validation_note",
                "Indiquer la vérification faite (texte de référence, date, taux contrôlés) — 5 caractères au moins.");
        var rule = await RuleQuery().Where(x => x.Id == ruleId && !x.IsTemplate).FirstOrDefaultAsync(ct);
        if (rule is null) return Error.NotFound("payroll.rule_not_found", "Règle introuvable.");
        if (rule.Status != "draft") return Error.Conflict("payroll.rule_frozen", "Seul un brouillon peut être validé.");
        if ((await config.ReadAsync(ct)).SegregationOfDuties && rule.CreatedBy == UserId)
            return Error.Forbidden("payroll.self_approval", "La règle doit être validée par une autre personne que son auteur (séparation des tâches).");
        await uow.ExecuteInTransactionAsync(async token =>
        {
            // La version précédente cède la place à la date d'effet de la nouvelle (jamais deux règles actives du même code).
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_rule
                   SET valid_to = {rule.ValidFrom.AddDays(-1)}
                 WHERE company_id = {Company} AND code = {rule.Code} AND id <> {ruleId} AND status = 'validated'
                   AND valid_from < {rule.ValidFrom} AND (valid_to IS NULL OR valid_to >= {rule.ValidFrom})
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_rule SET status = 'validated', validated_by = {UserId}, validated_at = now(), validation_note = {r.Note.Trim()}
                WHERE id = {ruleId} AND company_id = {Company} AND status = 'draft'
                """, token);
            return 0;
        }, ct);
        return Result.Success(ToDto(await RuleQuery().Where(x => x.Id == ruleId).FirstAsync(ct)));
    }

    public async Task<Result<PayrollRuleDto>> RetireAsync(Guid ruleId, RetireRuleRequest r, CancellationToken ct = default)
    {
        var rule = await RuleQuery().Where(x => x.Id == ruleId && !x.IsTemplate).FirstOrDefaultAsync(ct);
        if (rule is null) return Error.NotFound("payroll.rule_not_found", "Règle introuvable.");
        if (rule.Status != "validated") return Error.Conflict("payroll.rule_not_validated", "Seule une règle validée peut être retirée.");
        if (r.ValidTo < rule.ValidFrom.AddDays(-1)) return Error.Validation("payroll.rule_invalid", "Date de fin antérieure au début de la règle.");
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payroll_rule SET status = 'retired', valid_to = {r.ValidTo}
            WHERE id = {ruleId} AND company_id = {Company} AND status = 'validated'
            """, ct);
        return Result.Success(ToDto(await RuleQuery().Where(x => x.Id == ruleId).FirstAsync(ct)));
    }

    public async Task<Result<PayrollRuleDto>> AdoptTemplateAsync(Guid templateId, CancellationToken ct = default)
    {
        var t = await RuleQuery().Where(x => x.Id == templateId && x.IsTemplate).FirstOrDefaultAsync(ct);
        if (t is null) return Error.NotFound("payroll.rule_not_found", "Modèle introuvable.");
        var existing = await RuleQuery().Where(x => !x.IsTemplate && x.Code == t.Code).Select(x => (int?)x.Version).MaxAsync(ct);
        if (existing is not null) return Error.Conflict("payroll.rule_exists", "Une règle de même code existe déjà dans l'entreprise.");
        var request = new SavePayrollRuleRequest(t.Code, t.Name, t.Jurisdiction, t.RuleType, t.BaseType, t.Method, t.Rate, t.FixedAmount,
            ParseBrackets(t.Brackets), t.Annualize, t.BaseFloor, t.BaseCeiling, t.MinAmount, t.MaxAmount,
            await PayrollRules.TodayAsync(db, ct), null, t.LegalReference,
            "Adopté depuis le modèle — à vérifier au regard des textes en vigueur avant validation. " + t.Notes);
        var id = await InsertAsync(request, 1, null, ct);
        return Result.Success(ToDto(await RuleQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }
}
