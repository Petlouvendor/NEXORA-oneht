using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Parties;
using Nexora.Application.Security;
using Nexora.Contracts.Parties;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Parties;

/// <summary>Numérotation automatique des tiers (CLI-00001, FRN-00001) sans collision entre créations
/// simultanées : verrou consultatif par entreprise et par série, libéré à la fin de la transaction.</summary>
internal static class PartyNumbering
{
    public static Task LockAsync(NexoraDbContext db, string series, Guid companyId, CancellationToken ct)
    {
        var key = $"{series}:{companyId}";
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }
}

/// <summary>Règles communes aux fiches tiers : validation des champs de contact, devise, numérotation.</summary>
internal static class PartyRules
{
    public static readonly string[] Types = ["individual", "business"];

    public static Error? Validate(string? name, string? type, string? email)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            return Error.Validation("party.name", "Nom requis (200 caractères au plus).");
        if (!Types.Contains(type))
            return Error.Validation("party.type", "Type attendu : individual ou business.");
        if (!string.IsNullOrWhiteSpace(email) && (!email.Contains('@') || email.Trim().Length > 254))
            return Error.Validation("party.email", "Adresse email invalide.");
        return null;
    }

    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Fiche client (recette Phase 55 CLI-001/002/007 ; module clients §6, §17, §29). Même table que
/// le reste du produit — ventes, créances, relances, recommandations la lisent déjà.</summary>
public sealed class SqlCustomerService(NexoraDbContext db, ITenantContext tenant, IPermissionService permissions, IUnitOfWork uow) : ICustomerService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, string CustomerNumber, string Name, string Type, string? Phone, string? Email,
        string? Address, string? TaxIdentifier, string Status, decimal CreditLimit, string Currency, decimal OpenReceivables,
        DateTimeOffset CreatedAt, string? FirstName, string? City, string? Country, string? SecondaryContactName,
        string? SecondaryContactPhone, string? Segment, Guid? CustomerGroupId, string? CustomerGroupName, Guid? AccountManagerId,
        string? AccountManagerName, int? CreditDays, string? CreditTerms, Guid? EstablishmentId, string? EstablishmentName,
        string? Notes, string? CreatedByName, DateTimeOffset UpdatedAt);

    private IQueryable<Row> Query(Guid? id, string? q, string? status)
    {
        var digits = q is null ? "" : new string(q.Where(char.IsDigit).ToArray());
        var phone = digits.Length >= 3 ? digits : null;
        return db.Database.SqlQuery<Row>($"""
            SELECT c.id AS "Id", c.customer_number AS "CustomerNumber", c.name AS "Name", c.type AS "Type",
                   c.phone AS "Phone", c.email::text AS "Email", c.address AS "Address", c.tax_identifier AS "TaxIdentifier",
                   c.status AS "Status", c.credit_limit AS "CreditLimit", cur.code AS "Currency",
                   COALESCE((SELECT SUM(r.remaining_amount) FROM nexora.customer_receivable r
                             WHERE r.customer_id = c.id AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')), 0) AS "OpenReceivables",
                   c.created_at AS "CreatedAt", c.first_name AS "FirstName", c.city AS "City", c.country AS "Country",
                   c.secondary_contact_name AS "SecondaryContactName", c.secondary_contact_phone AS "SecondaryContactPhone",
                   c.segment AS "Segment", c.customer_group_id AS "CustomerGroupId", g.name AS "CustomerGroupName",
                   c.account_manager_id AS "AccountManagerId", am.full_name AS "AccountManagerName",
                   c.credit_days AS "CreditDays", c.credit_terms AS "CreditTerms", c.establishment_id AS "EstablishmentId",
                   e.name AS "EstablishmentName", c.notes AS "Notes", cb.full_name AS "CreatedByName", c.updated_at AS "UpdatedAt"
            FROM nexora.customer c
            JOIN nexora.currency cur ON cur.id = c.default_currency_id
            LEFT JOIN nexora.customer_group g ON g.id = c.customer_group_id
            LEFT JOIN nexora.app_user am ON am.id = c.account_manager_id
            LEFT JOIN nexora.app_user cb ON cb.id = c.created_by
            LEFT JOIN nexora.establishment e ON e.id = c.establishment_id
            WHERE c.company_id = {Company}
              AND ({id}::uuid IS NULL OR c.id = {id})
              AND ({status}::text IS NULL OR c.status = {status})
              AND ({q}::text IS NULL
                   OR nexora.search_norm(coalesce(c.first_name, '') || ' ' || c.name) LIKE '%' || nexora.search_norm({q}) || '%'
                   OR c.customer_number ILIKE '%' || {q} || '%' OR c.email::text ILIKE '%' || {q} || '%'
                   OR ({phone}::text IS NOT NULL AND regexp_replace(coalesce(c.phone, ''), '\D', '', 'g') LIKE '%' || {phone}::text || '%'))
            """);
    }

    private static CustomerDto ToDto(Row r, bool showCredit) => new(r.Id, r.CustomerNumber, r.Name, r.Type, r.Phone, r.Email, r.Address,
        r.TaxIdentifier, r.Status, showCredit ? r.CreditLimit : null, r.Currency, showCredit ? r.OpenReceivables : null, r.CreatedAt,
        r.FirstName, r.City, r.Country, r.SecondaryContactName, r.SecondaryContactPhone, r.Segment, r.CustomerGroupId,
        r.CustomerGroupName, r.AccountManagerId, r.AccountManagerName, showCredit ? r.CreditDays : null,
        showCredit ? r.CreditTerms : null, r.EstablishmentId, r.EstablishmentName, r.Notes, r.CreatedByName, r.UpdatedAt);

    /// <summary>
    /// Guide §7.2, §16 : les créances de chaque client, listées, ce sont les créances globales —
    /// réservées au portefeuille crédit. Le solde d'UN client reste lisible au POS avec
    /// <c>pos.view_customer_balance</c>, parce qu'il sert à décider une vente à crédit.
    /// </summary>
    private async Task<bool> ShowCreditAsync(bool singleCustomer, CancellationToken ct)
    {
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        return granted.Contains(Permissions.Credit.ViewPortfolio)
               || (singleCustomer && granted.Contains(Permissions.Pos.ViewCustomerBalance));
    }

    public async Task<IReadOnlyList<CustomerDto>> ListAsync(string? q, string? status, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(offset, 0);
        var rows = await Query(null, PartyRules.Clean(q), PartyRules.Clean(status))
            .OrderBy(r => r.Name).ThenBy(r => r.Id).Skip(offset).Take(limit).ToListAsync(ct);
        var showCredit = await ShowCreditAsync(singleCustomer: false, ct);
        return rows.Select(r => ToDto(r, showCredit)).ToList();
    }

    public async Task<CustomerDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var r = await Query(id, null, null).FirstOrDefaultAsync(ct);
        return r is null ? null : ToDto(r, await ShowCreditAsync(singleCustomer: true, ct));
    }

    private async Task<bool> CanSetCreditLimitAsync(CancellationToken ct)
    {
        if (tenant.UserId is not { } uid) return false;
        var profile = await permissions.LoadProfileAsync(uid, ct);
        return profile.PermissionsFor(Company).Contains(Permissions.Credit.SetLimit);
    }

    /// <summary>Contrôles communs création / modification des champs de la fiche (§6).</summary>
    private async Task<Error?> ValidateLinksAsync(Guid? groupId, Guid? managerId, Guid? establishmentId, int? creditDays, CancellationToken ct)
    {
        if (creditDays is < 0 or > 3650) return Error.Validation("customer.credit_days", "Durée de crédit entre 0 et 3650 jours.");
        if (groupId is { } g && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.customer_group WHERE id = {g} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("customer.group", "Groupe de clients introuvable.");
        if (establishmentId is { } e && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.establishment WHERE id = {e} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("customer.establishment", "Succursale introuvable.");
        if (managerId is { } m)
        {
            bool member;
            using (tenant.BeginSystemScope())
                member = await db.Database.SqlQuery<bool>($"""
                    SELECT EXISTS(SELECT 1 FROM nexora.user_role ur JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                                  WHERE ur.user_id = {m} AND ur.revoked_at IS NULL
                                    AND (sc.company_id = {Company}
                                         OR sc.group_id = (SELECT group_id FROM nexora.company WHERE id = {Company}))) AS "Value"
                    """).FirstAsync(ct);
            if (!member) return Error.Validation("customer.account_manager", "Le responsable doit être un utilisateur de l'entreprise.");
        }
        return null;
    }

    public async Task<Result<CustomerDto>> CreateAsync(CreateCustomerRequest r, CancellationToken ct = default)
    {
        var type = r.Type?.Trim().ToLowerInvariant() ?? "individual";
        if (PartyRules.Validate(r.Name, type, r.Email) is { } invalid) return invalid;
        if (r.CreditLimit < 0) return Error.Validation("customer.credit_limit", "La limite de crédit ne peut pas être négative.");
        if ((r.CreditLimit > 0 || r.CreditDays is not null || PartyRules.Clean(r.CreditTerms) is not null) && !await CanSetCreditLimitAsync(ct))
            return Error.Forbidden("customer.credit_limit_forbidden",
                "Ouvrir une limite ou fixer des conditions de crédit exige la permission credit.set_limit.");
        if (await ValidateLinksAsync(r.CustomerGroupId, r.AccountManagerId, r.EstablishmentId, r.CreditDays, ct) is { } linkError)
            return linkError;

        var currencyId = await db.Database.SqlQuery<Guid>($"""
            SELECT COALESCE((SELECT id FROM nexora.currency WHERE code = {PartyRules.Clean(r.Currency)}),
                            (SELECT default_currency_id FROM nexora.company WHERE id = {Company})) AS "Value"
            """).FirstAsync(ct);
        if (PartyRules.Clean(r.Currency) is { } code && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.currency WHERE code = {code}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("customer.currency", $"Devise « {code} » inconnue.");

        var requested = PartyRules.Clean(r.CustomerNumber);
        var id = Guid.NewGuid();
        try
        {
            // Numéro automatique « plus grand + 1 » : un verrou par entreprise, tenu jusqu'à la fin de la
            // transaction, empêche deux créations simultanées (deux caisses) d'obtenir le même numéro.
            await uow.ExecuteInTransactionAsync(async tct =>
            {
                var number = requested;
                if (number is null)
                {
                    await PartyNumbering.LockAsync(db, "customer_number", Company, tct);
                    number = await db.Database.SqlQuery<string>($"""
                        SELECT 'CLI-' || lpad((COALESCE(MAX(substring(customer_number FROM '^CLI-(\d+)$')::int), 0) + 1)::text, 5, '0') AS "Value"
                        FROM nexora.customer WHERE company_id = {Company}
                        """).FirstAsync(tct);
                }
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.customer (id, company_id, customer_number, name, type, phone, email, address, tax_identifier,
                                                 credit_limit, default_currency_id, first_name, city, country, secondary_contact_name,
                                                 secondary_contact_phone, segment, customer_group_id, account_manager_id, credit_days,
                                                 credit_terms, establishment_id, notes, created_by, updated_by)
                    VALUES ({id}, {Company}, {number}, {r.Name.Trim()}, {type}, {PartyRules.Clean(r.Phone)},
                            {PartyRules.Clean(r.Email)}::citext, {PartyRules.Clean(r.Address)}, {PartyRules.Clean(r.TaxIdentifier)},
                            {r.CreditLimit}, {currencyId}, {PartyRules.Clean(r.FirstName)}, {PartyRules.Clean(r.City)},
                            {PartyRules.Clean(r.Country)}, {PartyRules.Clean(r.SecondaryContactName)}, {PartyRules.Clean(r.SecondaryContactPhone)},
                            {PartyRules.Clean(r.Segment)}, {r.CustomerGroupId}, {r.AccountManagerId}, {r.CreditDays},
                            {PartyRules.Clean(r.CreditTerms)}, {r.EstablishmentId}, {PartyRules.Clean(r.Notes)}, {tenant.UserId}, {tenant.UserId})
                    """, tct);
                return number;
            }, ct);
        }
        catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505")
        {
            return Error.Conflict("customer.number_exists", $"Le numéro client « {requested} » existe déjà.");
        }
        return Result.Success((await GetAsync(id, ct))!);
    }

    public async Task<Result<CustomerDto>> UpdateAsync(Guid id, UpdateCustomerRequest r, CancellationToken ct = default)
    {
        var type = r.Type?.Trim().ToLowerInvariant() ?? "individual";
        if (PartyRules.Validate(r.Name, type, r.Email) is { } invalid) return invalid;
        var current = await Query(id, null, null).FirstOrDefaultAsync(ct);
        if (current is null) return Error.NotFound("customer.not_found", "Client introuvable.");

        var limit = r.CreditLimit ?? current.CreditLimit;
        var days = r.CreditDays ?? current.CreditDays;
        var terms = r.CreditTerms is null ? current.CreditTerms : PartyRules.Clean(r.CreditTerms);
        if (limit < 0) return Error.Validation("customer.credit_limit", "La limite de crédit ne peut pas être négative.");
        if ((limit != current.CreditLimit || days != current.CreditDays || terms != current.CreditTerms) && !await CanSetCreditLimitAsync(ct))
            return Error.Forbidden("customer.credit_limit_forbidden",
                "Modifier la limite ou les conditions de crédit exige la permission credit.set_limit.");
        // Champs du module clients (§6) : absents (null) = inchangés, pour qu'un écran qui ne les connaît
        // pas (fiche « Tiers ») ne les efface pas ; "" efface un texte, Guid.Empty efface un lien.
        static string? Keep(string? requested, string? existing) => requested is null ? existing : PartyRules.Clean(requested);
        static Guid? KeepId(Guid? requested, Guid? existing) => requested is null ? existing : requested == Guid.Empty ? null : requested;
        var group = KeepId(r.CustomerGroupId, current.CustomerGroupId);
        var manager = KeepId(r.AccountManagerId, current.AccountManagerId);
        var establishment = KeepId(r.EstablishmentId, current.EstablishmentId);
        if (await ValidateLinksAsync(group, manager, establishment, days, ct) is { } linkError)
            return linkError;

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer
            SET name = {r.Name.Trim()}, type = {type}, phone = {PartyRules.Clean(r.Phone)}, email = {PartyRules.Clean(r.Email)}::citext,
                address = {PartyRules.Clean(r.Address)}, tax_identifier = {PartyRules.Clean(r.TaxIdentifier)}, credit_limit = {limit},
                first_name = {Keep(r.FirstName, current.FirstName)}, city = {Keep(r.City, current.City)},
                country = {Keep(r.Country, current.Country)},
                secondary_contact_name = {Keep(r.SecondaryContactName, current.SecondaryContactName)},
                secondary_contact_phone = {Keep(r.SecondaryContactPhone, current.SecondaryContactPhone)},
                segment = {Keep(r.Segment, current.Segment)},
                customer_group_id = {group}, account_manager_id = {manager}, credit_days = {days},
                credit_terms = {terms}, establishment_id = {establishment}, notes = {Keep(r.Notes, current.Notes)},
                updated_by = {tenant.UserId}
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        return Result.Success((await GetAsync(id, ct))!);
    }

    public async Task<Result<CustomerDto>> PatchAsync(Guid id, PatchCustomerRequest r, CancellationToken ct = default)
    {
        var c = await Query(id, null, null).FirstOrDefaultAsync(ct);
        if (c is null) return Error.NotFound("customer.not_found", "Client introuvable.");
        // Un champ absent reste inchangé ; les champs de crédit gardent leur contrôle de permission.
        return await UpdateAsync(id, new UpdateCustomerRequest(
            r.Name ?? c.Name, r.Type ?? c.Type, r.Phone ?? c.Phone, r.Email ?? c.Email, r.Address ?? c.Address,
            r.TaxIdentifier ?? c.TaxIdentifier, r.CreditLimit, r.FirstName, r.City, r.Country,
            r.SecondaryContactName, r.SecondaryContactPhone, r.Segment, r.CustomerGroupId, r.AccountManagerId,
            r.CreditDays, r.CreditTerms, r.EstablishmentId, r.Notes), ct);
    }

    private sealed record Exposure(decimal Due, decimal StoreCredit);

    public async Task<Result<CustomerDto>> ChangeStatusAsync(Guid id, ChangeCustomerStatusRequest r, CancellationToken ct = default)
    {
        var target = r.Status?.Trim().ToLowerInvariant() ?? "";
        if (target is not ("active" or "inactive" or "archived"))
            return Error.Validation("customer.status", "Statut attendu : active, inactive ou archived (le blocage passe par /credit/blocks).");
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5)
            return Error.Validation("customer.status_reason", "Le motif du changement de statut est obligatoire.");
        var current = await Query(id, null, null).FirstOrDefaultAsync(ct);
        if (current is null) return Error.NotFound("customer.not_found", "Client introuvable.");
        if (current.Status == "blocked")
            return Error.Conflict("customer.blocked", "Client bloqué : levez d'abord le blocage (/credit/blocks).");
        if (target == "archived")
        {
            // §21 : un client archivé garde ses mouvements, mais on n'archive pas un compte qui n'est pas soldé.
            var exposure = await db.Database.SqlQuery<Exposure>($"""
                SELECT COALESCE((SELECT SUM(remaining_amount) FROM nexora.customer_receivable WHERE customer_id = {id}
                                 AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')), 0) AS "Due",
                       COALESCE((SELECT SUM(remaining_amount) FROM nexora.customer_credit_memo WHERE customer_id = {id}
                                 AND status IN ('available','partially_used')), 0) AS "StoreCredit"
                """).FirstAsync(ct);
            if (exposure.Due != 0m || exposure.StoreCredit != 0m)
                return Error.Conflict("customer.not_settled",
                    $"Compte non soldé (dû {exposure.Due}, avoir disponible {exposure.StoreCredit}) : archivage impossible.");
        }
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer SET status = {target}, updated_by = {tenant.UserId}, notes = concat_ws(E'\n', notes,
                   {$"[{DateTime.UtcNow:yyyy-MM-dd}] Statut {current.Status} → {target} : {r.Reason.Trim()}"})
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        return Result.Success((await GetAsync(id, ct))!);
    }
}

/// <summary>Fiche fournisseur (recette Phase 55 ACH-001/011).</summary>
public sealed class SqlSupplierService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : ISupplierService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, string SupplierNumber, string Name, string Type, string? Phone, string? Email,
        string? Address, string? TaxIdentifier, string Status, string Currency, decimal OpenPayables, DateTimeOffset CreatedAt);

    public async Task<SupplierDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var r = await db.Database.SqlQuery<Row>($"""
            SELECT s.id AS "Id", s.supplier_number AS "SupplierNumber", s.name AS "Name", s.type AS "Type",
                   s.phone AS "Phone", s.email::text AS "Email", s.address AS "Address", s.tax_identifier AS "TaxIdentifier",
                   s.status AS "Status", cur.code AS "Currency",
                   COALESCE((SELECT SUM(p.remaining_amount) FROM nexora.supplier_payable p
                             WHERE p.supplier_id = s.id AND p.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')), 0) AS "OpenPayables",
                   s.created_at AS "CreatedAt"
            FROM nexora.supplier s
            JOIN nexora.currency cur ON cur.id = s.default_currency_id
            WHERE s.company_id = {Company} AND s.id = {id}
            """).FirstOrDefaultAsync(ct);
        return r is null ? null : new SupplierDto(r.Id, r.SupplierNumber, r.Name, r.Type, r.Phone, r.Email, r.Address,
            r.TaxIdentifier, r.Status, r.Currency, r.OpenPayables, r.CreatedAt);
    }

    public async Task<Result<SupplierDto>> CreateAsync(CreateSupplierRequest r, CancellationToken ct = default)
    {
        var type = r.Type?.Trim().ToLowerInvariant() ?? "business";
        if (PartyRules.Validate(r.Name, type, r.Email) is { } invalid) return invalid;
        if (PartyRules.Clean(r.Currency) is { } code && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.currency WHERE code = {code}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("supplier.currency", $"Devise « {code} » inconnue.");

        var currencyId = await db.Database.SqlQuery<Guid>($"""
            SELECT COALESCE((SELECT id FROM nexora.currency WHERE code = {PartyRules.Clean(r.Currency)}),
                            (SELECT default_currency_id FROM nexora.company WHERE id = {Company})) AS "Value"
            """).FirstAsync(ct);
        var requested = PartyRules.Clean(r.SupplierNumber);
        var id = Guid.NewGuid();
        try
        {
            // Même garde que pour les clients : numéro automatique calculé sous verrou d'entreprise.
            await uow.ExecuteInTransactionAsync(async tct =>
            {
                var number = requested;
                if (number is null)
                {
                    await PartyNumbering.LockAsync(db, "supplier_number", Company, tct);
                    number = await db.Database.SqlQuery<string>($"""
                        SELECT 'FRN-' || lpad((COALESCE(MAX(substring(supplier_number FROM '^FRN-(\d+)$')::int), 0) + 1)::text, 5, '0') AS "Value"
                        FROM nexora.supplier WHERE company_id = {Company}
                        """).FirstAsync(tct);
                }
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.supplier (id, company_id, supplier_number, name, type, phone, email, address, tax_identifier, default_currency_id)
                    VALUES ({id}, {Company}, {number}, {r.Name.Trim()}, {type}, {PartyRules.Clean(r.Phone)},
                            {PartyRules.Clean(r.Email)}::citext, {PartyRules.Clean(r.Address)}, {PartyRules.Clean(r.TaxIdentifier)}, {currencyId})
                    """, tct);
                return number;
            }, ct);
        }
        catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505")
        {
            return Error.Conflict("supplier.number_exists", $"Le numéro fournisseur « {requested} » existe déjà.");
        }
        return Result.Success((await GetAsync(id, ct))!);
    }

    public async Task<Result<SupplierDto>> UpdateAsync(Guid id, UpdateSupplierRequest r, CancellationToken ct = default)
    {
        var type = r.Type?.Trim().ToLowerInvariant() ?? "business";
        if (PartyRules.Validate(r.Name, type, r.Email) is { } invalid) return invalid;
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.supplier
            SET name = {r.Name.Trim()}, type = {type}, phone = {PartyRules.Clean(r.Phone)}, email = {PartyRules.Clean(r.Email)}::citext,
                address = {PartyRules.Clean(r.Address)}, tax_identifier = {PartyRules.Clean(r.TaxIdentifier)}
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        if (n == 0) return Error.NotFound("supplier.not_found", "Fournisseur introuvable.");
        return Result.Success((await GetAsync(id, ct))!);
    }
}
