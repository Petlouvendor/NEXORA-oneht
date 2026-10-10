using Nexora.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Inventory;
using Nexora.Application.Migration;
using Nexora.Contracts.Migration;
using Nexora.Domain.Inventory;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Migration;

// =============================================================================
// Importeurs par type d'entité (Phase 39 §9, §11-14, §25, §28-29).
//  • ValidateAsync : contrôles sans écriture (§25)
//  • ProcessAsync  : applique (ou simule si ctx.DryRun) selon le mode (§29)
// =============================================================================

internal abstract class ImporterBase(NexoraDbContext db)
{
    protected readonly NexoraDbContext Db = db;

    protected static string? F(IReadOnlyDictionary<string, string?> m, string key)
        => m.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v!.Trim() : null;

    protected static RowMessageDto Err(string field, string msg) => new("error", field, msg);
    protected static RowMessageDto Warn(string field, string msg) => new("warning", field, msg);

    protected async Task<Guid> CompanyCurrencyAsync(Guid company, CancellationToken ct)
        => await Db.Database.SqlQuery<Guid>($"""
            SELECT default_currency_id AS "Value" FROM nexora.company WHERE id = {company}
            """).FirstAsync(ct);

    protected static RowProcessResult Rejected(params RowMessageDto[] msgs) => new("error", "none", null, null, null, null, msgs);
    protected static RowProcessResult Ok(string dedup, string match, params RowMessageDto[] msgs)
        => new("valid", match, dedup, null, null, null, msgs);
}

// ----------------------------------------------------------------- Clients (§12)

internal sealed class CustomerImporter(NexoraDbContext db) : ImporterBase(db), IEntityImporter
{
    public string Kind => "customers";

    public IReadOnlyList<TargetFieldDto> TargetFields =>
    [
        new("code", true, "text", "Code client (identifiant unique dans l'entreprise)", true),
        new("name", true, "text", "Raison sociale / nom", false),
        new("phone", false, "phone", "Téléphone", false),
        new("email", false, "email", "E-mail", false),
        new("address", false, "text", "Adresse", false),
        new("credit_limit", false, "decimal", "Limite de crédit", false),
        new("tax_id", false, "text", "Numéro fiscal", false),
        new("type", false, "text", "individual | business", false),
    ];

    public async Task<RowProcessResult> ValidateAsync(IReadOnlyDictionary<string, string?> m, ImportContext ctx, CancellationToken ct)
    {
        var msgs = new List<RowMessageDto>();
        var code = F(m, "code");
        var name = F(m, "name");
        if (code is null) msgs.Add(Err("code", "Code client obligatoire."));
        if (name is null) msgs.Add(Err("name", "Nom obligatoire."));
        if (F(m, "credit_limit") is { } cl && TabularParser.ParseDecimal(cl) is null)
            msgs.Add(Err("credit_limit", "Montant invalide."));
        if (F(m, "email") is { } e && !e.Contains('@')) msgs.Add(Warn("email", "E-mail douteux."));
        if (msgs.Any(x => x.Level == "error")) return Rejected(msgs.ToArray());

        var (match, _) = await MatchAsync(ctx.CompanyId, code!, F(m, "email"), F(m, "name"), ct);
        return new(msgs.Count > 0 ? "warning" : "valid", match, code, null, null, null, msgs);
    }

    public async Task<RowProcessResult> ProcessAsync(IReadOnlyDictionary<string, string?> m, ImportContext ctx, CancellationToken ct)
    {
        var code = F(m, "code")!;
        var (match, existingId) = await MatchAsync(ctx.CompanyId, code, F(m, "email"), F(m, "name"), ct);
        var decision = Decide(ctx.Mode, existingId is not null);
        if (decision is "skipped" or "rejected")
            return new(decision, match, code, existingId, null, null,
                [new(decision == "rejected" ? "error" : "info", "code", decision == "rejected" ? "Doublon rejeté." : "Client déjà présent — ignoré.")]);

        var credit = TabularParser.ParseDecimal(F(m, "credit_limit")) ?? 0m;
        var type = F(m, "type") is "business" or "individual" ? F(m, "type")! : "individual";

        if (existingId is { } id)
        {
            if (!ctx.DryRun)
                await Db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.customer SET name = {F(m, "name")}, phone = {F(m, "phone")}, email = {F(m, "email")},
                        address = {F(m, "address")}, tax_identifier = {F(m, "tax_id")}, credit_limit = {credit}, updated_at = now()
                    WHERE id = {id} AND company_id = {ctx.CompanyId}
                    """, ct);
            return new("updated", match, code, id, "customer", "updated", []);
        }

        var newId = Guid.NewGuid();
        if (!ctx.DryRun)
        {
            var currency = await CompanyCurrencyAsync(ctx.CompanyId, ct);
            await Db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.customer (id, company_id, customer_number, name, type, phone, email, address, tax_identifier, credit_limit, default_currency_id)
                VALUES ({newId}, {ctx.CompanyId}, {code}, {F(m, "name")}, {type}, {F(m, "phone")}, {F(m, "email")},
                        {F(m, "address")}, {F(m, "tax_id")}, {credit}, {currency})
                """, ct);
        }
        return new("created", "none", code, newId, "customer", "created", []);
    }

    private sealed record MatchRow(Guid Id, string Number, string? Email, string Name);

    private async Task<(string Match, Guid? Id)> MatchAsync(Guid company, string code, string? email, string? name, CancellationToken ct)
    {
        var exact = await Db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.customer WHERE company_id = {company} AND customer_number = {code}
            """).FirstOrDefaultAsync(ct);
        if (exact is not null) return ("exact", exact);

        if (email is not null)
        {
            var byEmail = await Db.Database.SqlQuery<Guid?>($"""
                SELECT id AS "Value" FROM nexora.customer WHERE company_id = {company} AND lower(email::text) = {email.ToLowerInvariant()}
                """).FirstOrDefaultAsync(ct);
            if (byEmail is not null) return ("probable", byEmail);
        }
        if (name is not null)
        {
            var byName = await Db.Database.SqlQuery<Guid?>($"""
                SELECT id AS "Value" FROM nexora.customer WHERE company_id = {company} AND lower(name) = {name.ToLowerInvariant()}
                """).FirstOrDefaultAsync(ct);
            if (byName is not null) return ("probable", byName);
        }
        return ("none", null);
    }

    internal static string Decide(string mode, bool exists) => mode switch
    {
        "create_only" when exists => "skipped",
        "update_only" when !exists => "skipped",
        "skip_duplicates" when exists => "skipped",
        "reject_duplicates" when exists => "rejected",
        _ => exists ? "updated" : "created",
    };
}

// ----------------------------------------------------------------- Fournisseurs (§13)

internal sealed class SupplierImporter(NexoraDbContext db) : ImporterBase(db), IEntityImporter
{
    public string Kind => "suppliers";

    public IReadOnlyList<TargetFieldDto> TargetFields =>
    [
        new("code", true, "text", "Code fournisseur", true),
        new("name", true, "text", "Raison sociale", false),
        new("phone", false, "phone", "Téléphone", false),
        new("email", false, "email", "E-mail", false),
        new("address", false, "text", "Adresse", false),
        new("credit_limit", false, "decimal", "Encours autorisé", false),
        new("tax_id", false, "text", "Numéro fiscal", false),
    ];

    public async Task<RowProcessResult> ValidateAsync(IReadOnlyDictionary<string, string?> m, ImportContext ctx, CancellationToken ct)
    {
        var msgs = new List<RowMessageDto>();
        if (F(m, "code") is null) msgs.Add(Err("code", "Code fournisseur obligatoire."));
        if (F(m, "name") is null) msgs.Add(Err("name", "Nom obligatoire."));
        if (F(m, "credit_limit") is { } cl && TabularParser.ParseDecimal(cl) is null) msgs.Add(Err("credit_limit", "Montant invalide."));
        if (msgs.Any(x => x.Level == "error")) return Rejected(msgs.ToArray());
        var id = await ExactAsync(ctx.CompanyId, F(m, "code")!, ct);
        return new(msgs.Count > 0 ? "warning" : "valid", id is null ? "none" : "exact", F(m, "code"), null, null, null, msgs);
    }

    public async Task<RowProcessResult> ProcessAsync(IReadOnlyDictionary<string, string?> m, ImportContext ctx, CancellationToken ct)
    {
        var code = F(m, "code")!;
        var existingId = await ExactAsync(ctx.CompanyId, code, ct);
        var decision = CustomerImporter.Decide(ctx.Mode, existingId is not null);
        if (decision is "skipped" or "rejected")
            return new(decision, existingId is null ? "none" : "exact", code, existingId, null, null,
                [new(decision == "rejected" ? "error" : "info", "code", decision == "rejected" ? "Doublon rejeté." : "Fournisseur déjà présent.")]);

        var credit = TabularParser.ParseDecimal(F(m, "credit_limit")) ?? 0m;
        if (existingId is { } id)
        {
            if (!ctx.DryRun)
                await Db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.supplier SET name = {F(m, "name")}, phone = {F(m, "phone")}, email = {F(m, "email")},
                        address = {F(m, "address")}, tax_identifier = {F(m, "tax_id")}, credit_limit = {credit}, updated_at = now()
                    WHERE id = {id} AND company_id = {ctx.CompanyId}
                    """, ct);
            return new("updated", "exact", code, id, "supplier", "updated", []);
        }

        var newId = Guid.NewGuid();
        if (!ctx.DryRun)
        {
            var currency = await CompanyCurrencyAsync(ctx.CompanyId, ct);
            await Db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.supplier (id, company_id, supplier_number, name, phone, email, address, tax_identifier, credit_limit, default_currency_id)
                VALUES ({newId}, {ctx.CompanyId}, {code}, {F(m, "name")}, {F(m, "phone")}, {F(m, "email")}, {F(m, "address")}, {F(m, "tax_id")}, {credit}, {currency})
                """, ct);
        }
        return new("created", "none", code, newId, "supplier", "created", []);
    }

    private async Task<Guid?> ExactAsync(Guid company, string code, CancellationToken ct)
        => await Db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.supplier WHERE company_id = {company} AND supplier_number = {code}
            """).FirstOrDefaultAsync(ct);
}

// ----------------------------------------------------------------- Produits (§11)

internal sealed class ProductImporter(NexoraDbContext db) : ImporterBase(db), IEntityImporter
{
    public string Kind => "products";

    public IReadOnlyList<TargetFieldDto> TargetFields =>
    [
        new("sku", true, "text", "Référence produit (SKU)", true),
        new("name", true, "text", "Désignation", false),
        new("barcode", false, "text", "Code-barres", false),
        new("category", false, "text", "Catégorie (créée si absente)", false),
        new("unit", false, "text", "Code unité (créée si absente)", false),
        new("sale_price", false, "decimal", "Prix de vente HT", false),
        new("cost", false, "decimal", "Coût / prix d'achat", false),
        new("product_type", false, "text", "stockable | service | weight", false),
        new("track_lots", false, "boolean", "Suivi des lots / péremption", false),
    ];

    public async Task<RowProcessResult> ValidateAsync(IReadOnlyDictionary<string, string?> m, ImportContext ctx, CancellationToken ct)
    {
        var msgs = new List<RowMessageDto>();
        if (F(m, "sku") is null) msgs.Add(Err("sku", "SKU obligatoire."));
        if (F(m, "name") is null) msgs.Add(Err("name", "Désignation obligatoire."));
        foreach (var f in new[] { "sale_price", "cost" })
            if (F(m, f) is { } v && TabularParser.ParseDecimal(v) is null) msgs.Add(Err(f, "Montant invalide."));
        if (F(m, "product_type") is { } pt && pt is not ("stockable" or "service" or "weight" or "non_stock" or "composite"))
            msgs.Add(Warn("product_type", "Type inconnu — « stockable » utilisé."));
        if (msgs.Any(x => x.Level == "error")) return Rejected(msgs.ToArray());
        var id = await BySkuAsync(ctx.CompanyId, F(m, "sku")!, ct);
        return new(msgs.Count > 0 ? "warning" : "valid", id is null ? "none" : "exact", F(m, "sku"), null, null, null, msgs);
    }

    public async Task<RowProcessResult> ProcessAsync(IReadOnlyDictionary<string, string?> m, ImportContext ctx, CancellationToken ct)
    {
        var sku = F(m, "sku")!;
        var existingId = await BySkuAsync(ctx.CompanyId, sku, ct);
        var decision = CustomerImporter.Decide(ctx.Mode, existingId is not null);
        if (decision is "skipped" or "rejected")
            return new(decision, existingId is null ? "none" : "exact", sku, existingId, null, null,
                [new(decision == "rejected" ? "error" : "info", "sku", decision == "rejected" ? "Doublon rejeté." : "Produit déjà présent.")]);

        var type = F(m, "product_type") is "stockable" or "service" or "weight" or "non_stock" or "composite" ? F(m, "product_type")! : "stockable";
        var trackLots = TabularParser.ParseBool(F(m, "track_lots")) ?? false;

        if (existingId is { } id)
        {
            if (!ctx.DryRun)
                await Db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.product SET name = {F(m, "name")}, barcode = {F(m, "barcode")}, product_type = {type},
                        track_lots = {trackLots}, updated_at = now()
                    WHERE id = {id} AND company_id = {ctx.CompanyId}
                    """, ct);
            await MaybePriceAsync(id, ctx, m, ct);
            return new("updated", "exact", sku, id, "product", "updated", []);
        }

        var newId = Guid.NewGuid();
        var msgs = new List<RowMessageDto>();
        if (!ctx.DryRun)
        {
            var unitId = await ResolveUnitAsync(ctx.CompanyId, F(m, "unit"), ct);
            var categoryId = await ResolveCategoryAsync(ctx.CompanyId, F(m, "category"), ctx.ImportJobId, ct);
            await Db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.product (id, company_id, sku, barcode, name, category_id, unit_id, product_type, track_lots, status)
                VALUES ({newId}, {ctx.CompanyId}, {sku}, {F(m, "barcode")}, {F(m, "name")}, {categoryId}, {unitId}, {type}, {trackLots}, 'active')
                """, ct);
            await MaybePriceAsync(newId, ctx, m, ct);
        }
        else if (F(m, "category") is { } cat && await CategoryIdAsync(ctx.CompanyId, cat, ct) is null)
            msgs.Add(Warn("category", $"Catégorie « {cat} » sera créée."));

        return new("created", "none", sku, newId, "product", "created", msgs);
    }

    private async Task MaybePriceAsync(Guid productId, ImportContext ctx, IReadOnlyDictionary<string, string?> m, CancellationToken ct)
    {
        if (ctx.DryRun) return;
        var sale = TabularParser.ParseDecimal(F(m, "sale_price"));
        if (sale is not { } price || price < 0m) return;
        var currency = await CompanyCurrencyAsync(ctx.CompanyId, ct);
        var exists = await Db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.product_price WHERE product_id = {productId} AND price_type = 'sale' AND currency_id = {currency} AND valid_to IS NULL) AS "Value"
            """).FirstAsync(ct);
        if (exists)
            await Db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.product_price SET amount = {price}, updated_at = now()
                WHERE product_id = {productId} AND price_type = 'sale' AND currency_id = {currency} AND valid_to IS NULL
                """, ct);
        else
            await Db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.product_price (product_id, company_id, price_type, currency_id, amount)
                VALUES ({productId}, {ctx.CompanyId}, 'sale', {currency}, {price})
                """, ct);
    }

    private async Task<Guid?> BySkuAsync(Guid company, string sku, CancellationToken ct)
        => await Db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.product WHERE company_id = {company} AND sku = {sku}
            """).FirstOrDefaultAsync(ct);

    private async Task<Guid?> CategoryIdAsync(Guid company, string name, CancellationToken ct)
        => await Db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.product_category WHERE company_id = {company} AND lower(name) = {name.Trim().ToLowerInvariant()} LIMIT 1
            """).FirstOrDefaultAsync(ct);

    private async Task<Guid?> ResolveCategoryAsync(Guid company, string? name, Guid importJobId, CancellationToken ct)
    {
        if (name is null) return null;
        var existing = await CategoryIdAsync(company, name, ct);
        if (existing is not null) return existing;
        var id = Guid.NewGuid();
        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.product_category (id, company_id, name) VALUES ({id}, {company}, {name.Trim()})
            """, ct);
        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.import_record (import_job_id, company_id, entity_type, entity_id, action)
            VALUES ({importJobId}, {company}, 'product_category', {id}, 'created')
            """, ct);
        return id;
    }

    private async Task<Guid> ResolveUnitAsync(Guid company, string? code, CancellationToken ct)
    {
        code = string.IsNullOrWhiteSpace(code) ? "UN" : code.Trim().ToUpperInvariant();
        var existing = await Db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.unit WHERE company_id = {company} AND upper(code) = {code}
            """).FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;
        var newId = Guid.NewGuid();
        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.unit (id, company_id, name, code) VALUES ({newId}, {company}, {code}, {code})
            """, ct);
        return newId;
    }
}

// ----------------------------------------------------------------- Stock d'ouverture (§14)

internal sealed class OpeningStockImporter(NexoraDbContext db, IStockLedger stock) : ImporterBase(db), IEntityImporter
{
    public string Kind => "opening_stock";

    public IReadOnlyList<TargetFieldDto> TargetFields =>
    [
        new("sku", true, "text", "SKU du produit (doit exister)", true),
        new("location", true, "text", "Code emplacement ou entrepôt", true),
        new("quantity", true, "decimal", "Quantité en stock", false),
        new("cost", false, "decimal", "Coût unitaire", false),
        new("count_date", false, "date", "Date d'inventaire", false),
    ];

    public async Task<RowProcessResult> ValidateAsync(IReadOnlyDictionary<string, string?> m, ImportContext ctx, CancellationToken ct)
    {
        var msgs = new List<RowMessageDto>();
        var sku = F(m, "sku");
        var loc = F(m, "location");
        var qty = TabularParser.ParseDecimal(F(m, "quantity"));
        if (sku is null) msgs.Add(Err("sku", "SKU obligatoire."));
        if (loc is null) msgs.Add(Err("location", "Emplacement obligatoire."));
        if (qty is null) msgs.Add(Err("quantity", "Quantité invalide."));
        else if (qty <= 0m) msgs.Add(Err("quantity", "Quantité d'ouverture doit être > 0."));

        if (sku is not null && await ProductIdAsync(ctx.CompanyId, sku, ct) is null)
            msgs.Add(Err("sku", $"Produit « {sku} » introuvable — importez d'abord les produits."));
        if (loc is not null && await LocationIdAsync(ctx.CompanyId, loc, ct) is null)
            msgs.Add(Err("location", $"Emplacement « {loc} » introuvable."));

        if (msgs.Any(x => x.Level == "error")) return Rejected(msgs.ToArray());
        return new("valid", "none", $"{sku}|{loc}", null, null, null, msgs);
    }

    public async Task<RowProcessResult> ProcessAsync(IReadOnlyDictionary<string, string?> m, ImportContext ctx, CancellationToken ct)
    {
        var sku = F(m, "sku")!;
        var loc = F(m, "location")!;
        var qty = TabularParser.ParseDecimal(F(m, "quantity")) ?? 0m;
        var cost = TabularParser.ParseDecimal(F(m, "cost")) ?? 0m;
        var productId = await ProductIdAsync(ctx.CompanyId, sku, ct);
        var locationId = await LocationIdAsync(ctx.CompanyId, loc, ct);
        if (productId is not { } pid || locationId is not { } lid)
            return Rejected(Err("sku", "Produit ou emplacement introuvable."));

        var alreadyOpen = await Db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.stock_movement
                          WHERE company_id = {ctx.CompanyId} AND product_id = {pid} AND inventory_location_id = {lid}
                            AND movement_type = 'opening') AS "Value"
            """).FirstAsync(ct);
        if (alreadyOpen)
            return new("skipped", "exact", $"{sku}|{loc}", pid, null, null,
                [new("info", "sku", "Stock d'ouverture déjà enregistré pour ce produit / emplacement.")]);

        if (!ctx.DryRun)
        {
            var occurred = TabularParser.ParseDate(F(m, "count_date")) is { } d
                ? BusinessDate.Noon(d) : (DateTimeOffset?)null;
            await stock.PostAsync(new StockMovementRequest(pid, lid, MovementType.Opening, qty, cost,
                ReferenceType: "import", ReferenceId: ctx.ImportJobId, OccurredAt: occurred), ct);
        }
        return new("created", "none", $"{sku}|{loc}", pid, "stock_movement", "created",
            [new("info", "quantity", $"Ouverture {qty:0.###} @ {cost:0.##}")]);
    }

    private async Task<Guid?> ProductIdAsync(Guid company, string sku, CancellationToken ct)
        => await Db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.product WHERE company_id = {company} AND sku = {sku}
            """).FirstOrDefaultAsync(ct);

    private async Task<Guid?> LocationIdAsync(Guid company, string code, CancellationToken ct)
    {
        var key = code.Trim().ToUpperInvariant();
        var direct = await Db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.inventory_location
            WHERE company_id = {company} AND (upper(code) = {key} OR upper(name) = {key}) LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (direct is not null) return direct;
        // Sinon, un entrepôt dont le code / nom correspond → son emplacement de type « warehouse ».
        return await Db.Database.SqlQuery<Guid?>($"""
            SELECT il.id AS "Value" FROM nexora.inventory_location il
            JOIN nexora.warehouse w ON w.id = il.warehouse_id AND w.company_id = il.company_id
            WHERE il.company_id = {company} AND (upper(w.code) = {key} OR upper(w.name) = {key}) LIMIT 1
            """).FirstOrDefaultAsync(ct);
    }
}
