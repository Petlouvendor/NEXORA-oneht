using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Customers;
using Nexora.Application.Printing;
using Nexora.Application.Purchasing;
using Nexora.Contracts.Customers;
using Nexora.Contracts.Printing;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using K = Nexora.Contracts.Printing.PrintKinds;

namespace Nexora.Infrastructure.Printing;

/// <summary>
/// Reçus et documents imprimables construits depuis la base (jamais depuis ce que le navigateur envoie).
/// Les chiffres « total versé / reste à verser » d'un reçu sont ceux du moment de l'opération (journal
/// client arrêté à l'écriture du versement) : une réimpression redonne les mêmes montants, marquée
/// « DUPLICATA ». Droits : <see cref="PrintAccess"/> ; une pièce hors périmètre répond « introuvable ».
/// </summary>
public sealed partial class SqlReceiptService(
    NexoraDbContext db, ITenantContext tenant, IPermissionService permissions, IPrintService print,
    ICustomerAccountQueries customers, ICustomerRefundService refunds, ICustomerExpenseService expenses,
    IPurchasingQueries purchasing, IDateTimeProvider clock) : IReceiptService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Built(PrintDocument Doc, string FileName, string Sensitivity = "financial", Guid? EstablishmentId = null);

    public async Task<Result<RenderedFile>> PrintAsync(string kind, Guid id, string format, CancellationToken ct = default)
    {
        var f = PrintFormats.Normalize(format);
        if (!PrintFormats.IsValid(f)) return Error.Validation("print.format", "Format inconnu : pdf, ticket, xlsx ou csv.");
        if (!PrintableKinds.All.Contains(kind)) return Error.NotFound("print.unknown_kind", $"Type de document « {kind} » inconnu.");
        var level = await LevelAsync(kind, ct);
        if (level == "none") return Error.Forbidden("print.forbidden", "Vous n'avez pas le droit d'imprimer ce document.");

        var built = kind switch
        {
            PrintableKinds.CustomerPayment => await CustomerPaymentAsync(id, level, ct),
            PrintableKinds.CustomerCredit => await CustomerCreditAsync(id, ct),
            PrintableKinds.CreditMemo => await CreditMemoAsync(id, ct),
            PrintableKinds.CustomerRefund => await CustomerRefundAsync(id, ct),
            PrintableKinds.CustomerExpense => await CustomerExpenseAsync(id, level, ct),
            PrintableKinds.Sale => await SaleAsync(id, ct),
            PrintableKinds.SupplierPayment => await SupplierPaymentAsync(id, ct),
            PrintableKinds.SupplierInvoice => await SupplierInvoiceAsync(id, ct),
            PrintableKinds.PurchaseOrder => await PurchaseOrderAsync(id, ct),
            PrintableKinds.GoodsReceipt => await GoodsReceiptAsync(id, ct),
            _ => null,
        };
        if (built is null) return Error.NotFound("print.not_found", "Document introuvable.");

        // Toute nouvelle sortie d'une même pièce est une copie : elle le dit (anti-fraude).
        var logKey = $"receipt:{kind}";
        var previous = await print.PreviousPrintsAsync(logKey, id, ct);
        var doc = built.Doc;
        if (previous > 0)
            doc = doc with { Stamp = doc.Stamp is null ? $"DUPLICATA — copie n° {previous}" : $"{doc.Stamp} · DUPLICATA n° {previous}" };
        return Result.Success(await print.RenderAsync(doc, f, logKey, built.FileName, built.Sensitivity, id, built.EstablishmentId, ct));
    }

    private async Task<string> LevelAsync(string kind, CancellationToken ct)
    {
        var perms = await permissions.CurrentPermissionsAsync(tenant, ct);
        return PrintAccess.Level(kind, p => perms.Contains(p) && (tenant.ApiScopes is null || tenant.ApiScopes.Contains(p)));
    }

    // ================================================================== Versement client

    private async Task<Built?> CustomerPaymentAsync(Guid id, string level, CancellationToken ct)
    {
        var p = await customers.PaymentAsync(id, ct);
        if (p is null) return null;
        if (level == "own" && !await CreatedByMeAsync("payment", id, ct)) return null;

        var mark = await db.Database.SqlQuery<DateTimeOffset?>($"""
            SELECT max(created_at) AS "Value" FROM nexora.customer_ledger_entry
            WHERE company_id = {Company} AND payment_id = {id} AND entry_type IN ('payment', 'overpayment')
            """).FirstOrDefaultAsync(ct);
        var at = mark ?? p.ReceivedAt;
        var pos = await PositionAsync(p.CustomerId, p.Currency, at, ct);
        var remaining = await RemainingAtAsync(p.Allocations.Select(a => a.ReceivableId).Distinct().ToArray(), at, ct);
        var originals = await OriginalsAsync(p.Allocations.Select(a => a.ReceivableId).Distinct().ToArray(), ct);
        var party = await CustomerPartyAsync(p.CustomerId, ct);

        var rows = p.Allocations.Select(a => (IReadOnlyList<object?>)
        [
            a.Voided ? $"{a.CreditNumber} (annulé)" : a.CreditNumber,
            originals.GetValueOrDefault(a.ReceivableId),
            a.Amount,
            remaining.GetValueOrDefault(a.ReceivableId),
        ]).ToList();

        var notes = new List<string>();
        if (p.UnallocatedAmount > 0 && p.CreditMemoNumber is not null)
            notes.Add($"Excédent de {PrintValues.Money(p.UnallocatedAmount)} {p.Currency} conservé en avoir {p.CreditMemoNumber}.");
        if (p.Status == "void")
            notes.Add($"Versement annulé{(p.VoidedByName is null ? "" : $" par {p.VoidedByName}")} — motif : {p.VoidReason}.");
        notes.Add("Situation du compte arrêtée au moment de ce versement.");

        var doc = new PrintDocument(
            Title: "Reçu de versement",
            Reference: p.PaymentNumber,
            Parties: party is null ? null : [party],
            Details: Details(
                ("Date", p.ReceivedAt, K.DateTime),
                ("Mode de paiement", p.PaymentMethod, K.Text),
                ("Caisse / compte", p.FinancialAccountName, K.Text),
                ("Référence", p.Reference, K.Text),
                ("Reçu par", p.ReceivedByName, K.Text),
                ("Note", p.Note, K.Text),
                ("Annulé le", p.VoidedAt, K.DateTime)),
            Tables: rows.Count == 0 ? null :
            [
                new PrintTable("Crédits réglés",
                    [new("Crédit"), new("Montant du crédit", K.Money, p.Currency), new("Payé ici", K.Money, p.Currency), new("Reste dû", K.Money, p.Currency)],
                    rows),
            ],
            Summary: PositionFigures(p.Amount, "Montant de ce versement", pos, p.Currency),
            Notes: notes,
            Signatures: ["Signature du client", "Le caissier"],
            Stamp: p.Status == "void" ? "VERSEMENT ANNULÉ" : null);
        return new Built(doc, $"recu-versement-{p.PaymentNumber}", EstablishmentId: await PaymentEstablishmentAsync(id, ct));
    }

    /// <summary>Position d'un compte client à un instant, dans une devise, d'après le journal immuable.</summary>
    private sealed record Position(decimal Debts, decimal Paid, decimal Balance);

    private async Task<Position> PositionAsync(Guid customerId, string currency, DateTimeOffset at, CancellationToken ct) =>
        await db.Database.SqlQuery<Position>($"""
            SELECT coalesce(sum(l.debit) FILTER (WHERE l.entry_type IN ('credit_sale', 'customer_expense', 'opening', 'manual_debit')), 0) AS "Debts",
                   coalesce(sum(l.credit) FILTER (WHERE l.entry_type IN ('payment', 'overpayment')), 0)
                     - coalesce(sum(l.debit) FILTER (WHERE l.entry_type = 'payment_void'), 0) AS "Paid",
                   coalesce(sum(l.debit - l.credit), 0) AS "Balance"
            FROM nexora.customer_ledger_entry l
            JOIN nexora.currency c ON c.id = l.currency_id
            WHERE l.company_id = {Company} AND l.customer_id = {customerId} AND c.code = {currency} AND l.created_at <= {at}
            """).FirstAsync(ct);

    /// <summary>« Total des crédits / total versé / reste à verser » (ou avoir si le solde est créditeur).</summary>
    private static List<PrintFigure> PositionFigures(decimal? amount, string amountLabel, Position pos, string currency)
    {
        var figures = new List<PrintFigure>();
        if (amount is { } a) figures.Add(new(amountLabel, a, K.Money, currency));
        figures.Add(new("Total des crédits", pos.Debts, K.Money, currency));
        figures.Add(new("Total versé", pos.Paid, K.Money, currency));
        var others = pos.Debts - pos.Paid - pos.Balance;
        if (Math.Abs(others) >= 0.005m) figures.Add(new("Retours, avoirs et ajustements", others, K.Money, currency));
        figures.Add(pos.Balance >= 0
            ? new("Reste à verser", pos.Balance, K.Money, currency, Emphasis: true)
            : new("Avoir en faveur du client", -pos.Balance, K.Money, currency, Emphasis: true));
        return figures;
    }

    private sealed record AmountRow(Guid Id, decimal Amount);

    /// <summary>Reste dû de chaque crédit à un instant : montant − versements, avoirs imputés et radiations
    /// actifs à cet instant (même règle que <c>recompute_customer_receivable</c>).</summary>
    private async Task<Dictionary<Guid, decimal>> RemainingAtAsync(Guid[] ids, DateTimeOffset at, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var rows = await db.Database.SqlQuery<AmountRow>($"""
            SELECT r.id AS "Id",
                   r.original_amount
                   - coalesce((SELECT sum(a.allocated_amount) FROM nexora.payment_allocation a
                               WHERE a.receivable_id = r.id AND a.allocated_at <= {at}
                                 AND (a.voided_at IS NULL OR a.voided_at > {at})), 0)
                   - coalesce((SELECT sum(x.amount) FROM nexora.customer_credit_application x
                               WHERE x.receivable_id = r.id AND x.applied_at <= {at}
                                 AND (x.voided_at IS NULL OR x.voided_at > {at})), 0)
                   - coalesce((SELECT sum(w.amount) FROM nexora.receivable_write_off w
                               WHERE w.receivable_id = r.id AND w.written_off_at <= {at}), 0) AS "Amount"
            FROM nexora.customer_receivable r
            WHERE r.company_id = {Company} AND r.id = ANY({ids})
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.Amount);
    }

    private async Task<Dictionary<Guid, decimal>> OriginalsAsync(Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var rows = await db.Database.SqlQuery<AmountRow>($"""
            SELECT id AS "Id", original_amount AS "Amount" FROM nexora.customer_receivable
            WHERE company_id = {Company} AND id = ANY({ids})
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.Amount);
    }

    private async Task<Guid?> PaymentEstablishmentAsync(Guid paymentId, CancellationToken ct) =>
        await db.Database.SqlQuery<Guid?>($"""
            SELECT st.establishment_id AS "Value"
            FROM nexora.payment p
            JOIN nexora.cash_session cs ON cs.id = p.cash_session_id
            JOIN nexora.cash_register cr ON cr.id = cs.cash_register_id
            JOIN nexora.store st ON st.id = cr.store_id
            WHERE p.id = {paymentId} AND p.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    /// <summary>Niveau « own » : la pièce doit avoir été enregistrée par l'utilisateur courant.</summary>
    private async Task<bool> CreatedByMeAsync(string table, Guid id, CancellationToken ct)
    {
        if (tenant.UserId is not { } me) return false;
        return table switch
        {
            "payment" => await db.Database.SqlQuery<bool>($"""
                SELECT coalesce(created_by = {me}, false) AS "Value" FROM nexora.payment WHERE id = {id} AND company_id = {Company}
                """).FirstOrDefaultAsync(ct),
            "customer_expense" => await db.Database.SqlQuery<bool>($"""
                SELECT coalesce(created_by = {me}, false) AS "Value" FROM nexora.customer_expense WHERE id = {id} AND company_id = {Company}
                """).FirstOrDefaultAsync(ct),
            _ => false,
        };
    }

    // ================================================================== Crédit, avoir, remboursement, dépense

    private async Task<Built?> CustomerCreditAsync(Guid id, CancellationToken ct)
    {
        var detail = await customers.CreditAsync(id, ct);
        if (detail is null) return null;
        var c = detail.Credit;
        var party = await CustomerPartyAsync(c.CustomerId, ct);

        var tables = new List<PrintTable>
        {
            new("Règlements",
                [new("Date", K.DateTime), new("Nature"), new("Référence"), new("Mode"), new("Montant", K.Money, c.Currency), new("État")],
                detail.Settlements.Select(s => (IReadOnlyList<object?>)
                    [s.At, SettlementLabel(s.Kind), s.Reference, s.Method, s.Amount, s.Voided ? "Annulé" : "Actif"]).ToList(),
                [null, null, null, "Total réglé", c.PaidAmount, null]),
        };
        if (detail.Schedule is { } schedule)
            tables.Add(new PrintTable($"Échéancier (version {schedule.Version})",
                [new("N°", K.Integer), new("Échéance", K.Date), new("Montant", K.Money, c.Currency), new("Couvert", K.Money, c.Currency),
                 new("Reste", K.Money, c.Currency), new("État")],
                schedule.Installments.Select(i => (IReadOnlyList<object?>)
                    [i.Number, i.DueDate, i.Amount, i.Covered, i.Remaining, InstallmentLabel(i.Status)]).ToList()));

        var doc = new PrintDocument(
            Title: "Crédit client — reconnaissance de dette",
            Reference: c.CreditNumber,
            Parties: party is null ? null : [party],
            Details: Details(
                ("Origine", $"{SourceLabel(c.SourceType)}{(c.SourceReference is null ? "" : $" {c.SourceReference}")}", K.Text),
                ("Date d'émission", c.IssueDate, K.Date),
                ("Échéance", c.DueDate, K.Date),
                ("Statut", CreditStatusLabel(c.DisplayStatus) + (c.DaysOverdue > 0 ? $" ({c.DaysOverdue} j de retard)" : ""), K.Text),
                ("Établissement", c.EstablishmentName, K.Text),
                ("Créé par", c.CreatedByName, K.Text)),
            Tables: tables,
            Summary:
            [
                new("Montant du crédit", c.OriginalAmount, K.Money, c.Currency),
                new("Total réglé", c.PaidAmount, K.Money, c.Currency),
                new("Reste à payer", c.RemainingAmount, K.Money, c.Currency, Emphasis: true),
            ],
            Notes: ["Le client reconnaît devoir la somme restant à payer ci-dessus, aux échéances indiquées."],
            Signatures: ["Signature du client (bon pour reconnaissance de dette)", "Pour l'entreprise"],
            Stamp: c.DisplayStatus switch { "cancelled" => "CRÉDIT ANNULÉ", "written_off" => "CRÉDIT RADIÉ", "paid" => "SOLDÉ", _ => null });
        return new Built(doc, $"credit-{c.CreditNumber}");
    }

    private async Task<Built?> CreditMemoAsync(Guid id, CancellationToken ct)
    {
        var m = await customers.CreditMemoAsync(id, ct);
        if (m is null) return null;
        var party = await CustomerPartyAsync(m.CustomerId, ct);
        var doc = new PrintDocument(
            Title: "Avoir client",
            Reference: m.MemoNumber,
            Parties: party is null ? null : [party],
            Details: Details(
                ("Origine", m.SourceType == "sale_return" ? $"Retour {m.SourceReference}" : $"Excédent de versement {m.SourceReference}", K.Text),
                ("Date", m.CreatedAt, K.DateTime),
                ("Motif", m.Reason, K.Text),
                ("Émis par", m.CreatedByName, K.Text)),
            Tables:
            [
                new PrintTable("Utilisations",
                    [new("Date", K.DateTime), new("Nature"), new("Référence"), new("Montant", K.Money, m.Currency), new("État")],
                    m.Uses.Select(u => (IReadOnlyList<object?>)
                        [u.At, u.Kind == "refund" ? "Remboursement" : "Imputé sur un crédit", u.Reference, u.Amount, u.Voided ? "Annulé" : "Actif"]).ToList()),
            ],
            Summary:
            [
                new("Montant de l'avoir", m.Amount, K.Money, m.Currency),
                new("Déjà utilisé", m.UsedAmount, K.Money, m.Currency),
                new("Disponible", m.RemainingAmount, K.Money, m.Currency, Emphasis: true),
            ],
            Notes: ["L'avoir disponible peut être utilisé en paiement à la caisse ou sur un crédit, ou remboursé après validation."],
            Stamp: m.Status == "cancelled" ? "AVOIR ANNULÉ" : null);
        return new Built(doc, $"avoir-{m.MemoNumber}");
    }

    private async Task<Built?> CustomerRefundAsync(Guid id, CancellationToken ct)
    {
        var r = await refunds.GetAsync(id, ct);
        if (r is null) return null;
        var party = await CustomerPartyAsync(r.CustomerId, ct);
        var doc = new PrintDocument(
            Title: "Reçu de remboursement",
            Reference: r.RefundNumber,
            Parties: party is null ? null : [party],
            Details: Details(
                ("Avoir remboursé", r.MemoNumber, K.Text),
                ("Mode", RefundMethodLabel(r.RefundMethod), K.Text),
                ("Motif", r.Reason, K.Text),
                ("Demandé le", r.RequestedAt, K.DateTime),
                ("Demandé par", r.RequestedByName, K.Text),
                ("Approuvé par", r.DecidedByName, K.Text),
                ("Décaissé le", r.ExecutedAt, K.DateTime),
                ("Décaissé par", r.ExecutedByName, K.Text)),
            Summary: [new("Montant remboursé", r.Amount, K.Money, r.Currency, Emphasis: true)],
            Signatures: ["Signature du client (reçu la somme)", "Le caissier"],
            Stamp: r.Status switch
            {
                "executed" => null,
                "rejected" => "REMBOURSEMENT REFUSÉ",
                "cancelled" => "REMBOURSEMENT ANNULÉ",
                _ => "NON DÉCAISSÉ — en attente",
            });
        return new Built(doc, $"remboursement-{r.RefundNumber}");
    }

    private async Task<Built?> CustomerExpenseAsync(Guid id, string level, CancellationToken ct)
    {
        var e = await expenses.GetAsync(id, ct);
        if (e is null) return null;
        if (level == "own" && !await CreatedByMeAsync("customer_expense", id, ct)) return null;
        var party = await CustomerPartyAsync(e.CustomerId, ct);
        var doc = new PrintDocument(
            Title: "Note de dépense imputée",
            Reference: e.ExpenseNumber,
            Parties: party is null ? null : [party],
            Details: Details(
                ("Date", e.ExpenseDate, K.Date),
                ("Catégorie", e.Category, K.Text),
                ("Refacturée au client", e.Billable ? "Oui" : "Non", K.Text),
                ("Échéance", e.DueDate, K.Date),
                ("Crédit", e.CreditNumber, K.Text),
                ("Saisie par", e.CreatedByName, K.Text),
                ("Validée par", e.DecidedByName, K.Text)),
            Tables: [new PrintTable(null, [new("Description"), new("Montant", K.Money, e.Currency)], [[e.Description, e.Amount]])],
            Summary: [new(e.Billable ? "Montant dû par le client" : "Montant", e.Amount, K.Money, e.Currency, Emphasis: true)],
            Stamp: e.Status switch
            {
                "validated" => null,
                "cancelled" => "DÉPENSE ANNULÉE",
                "rejected" => "DÉPENSE REFUSÉE",
                _ => "EN ATTENTE DE VALIDATION",
            });
        return new Built(doc, $"depense-{e.ExpenseNumber}");
    }

    // ================================================================== Relevé client

    public async Task<Result<RenderedFile>> CustomerStatementAsync(
        Guid customerId, DateOnly? from, DateOnly? to, string? currency, string format, CancellationToken ct = default)
    {
        var f = PrintFormats.Normalize(format);
        if (!PrintFormats.IsValid(f)) return Error.Validation("print.format", "Format inconnu : pdf, ticket, xlsx ou csv.");
        if (await LevelAsync("customer-statement", ct) == "none")
            return Error.Forbidden("print.forbidden", "Le relevé de compte requiert credit.view_portfolio.");

        // Ticket de caisse : sans période demandée, les 3 derniers mois (solde reporté en tête) — un rouleau
        // thermique ne doit pas dérouler tout l'historique du client.
        var ticket = f == PrintFormats.Ticket;
        var autoPeriod = ticket && from is null && to is null;
        if (autoPeriod) from = (await CompanyTodayAsync(ct)).AddDays(-TicketStatementDays);

        var summary = await customers.SummaryAsync(customerId, from, to, ct);
        if (summary is null) return Error.NotFound("print.not_found", "Client introuvable.");
        var cur = string.IsNullOrWhiteSpace(currency) ? summary.Currency : currency.Trim().ToUpperInvariant();
        var ledger = await customers.LedgerAsync(customerId, from, to, cur, ct);
        if (ledger is null) return Error.NotFound("print.not_found", "Client introuvable.");
        var party = await CustomerPartyAsync(customerId, ct);
        var balance = summary.Balances.FirstOrDefault(b => b.Currency == cur);

        if (ticket)
        {
            var ticketDoc = StatementTicket(summary, ledger, balance, party, cur, from, to, autoPeriod);
            return Result.Success(await print.RenderAsync(ticketDoc, f, "statement:customer", $"releve-{summary.CustomerNumber}", "financial", customerId, ct: ct));
        }

        var rows = new List<IReadOnlyList<object?>>();
        if (from is not null) rows.Add([from, "Solde d'ouverture", null, null, null, ledger.OpeningBalance]);
        rows.AddRange(ledger.Entries.Select(e => (IReadOnlyList<object?>)
            [e.OccurredAt, e.Label, e.Reference, e.Debit == 0 ? null : e.Debit, e.Credit == 0 ? null : e.Credit, e.Balance]));

        var figures = new List<PrintFigure>();
        if (cur == summary.Currency)
        {
            figures.Add(new("Total des achats", summary.TotalPurchases, K.Money, cur));
            figures.Add(new("Dont à crédit", summary.CreditPurchases, K.Money, cur));
            figures.Add(new("Total versé", summary.TotalPaid, K.Money, cur));
        }
        figures.Add(new("Avoir disponible", balance?.AvailableStoreCredit ?? summary.AvailableStoreCredit, K.Money, cur));
        figures.Add(new("Montant en retard", balance?.OverdueAmount ?? summary.OverdueAmount, K.Money, cur));
        figures.Add(new("Reste à verser", balance?.BalanceDue ?? summary.BalanceDue, K.Money, cur, Emphasis: true));

        var doc = new PrintDocument(
            Title: "Relevé de compte client",
            Reference: summary.CustomerNumber,
            Subtitle: Period(from, to),
            Parties: party is null ? null : [party],
            Details:
            [
                .. Details(("Devise", cur, K.Text)),
                .. cur == summary.Currency ? [new PrintFigure("Limite de crédit", summary.CreditLimit, K.Money, cur)] : Array.Empty<PrintFigure>(),
                .. Details(("Crédits en retard", summary.OverdueCredits, K.Integer), ("Dernier versement", summary.LastPaymentAt, K.Date)),
            ],
            Tables:
            [
                new PrintTable("Mouvements du compte",
                    [new("Date", K.DateTime), new("Opération"), new("Référence"), new("Débit", K.Money, cur), new("Crédit", K.Money, cur), new("Solde", K.Money, cur)],
                    rows, [null, "Totaux", null, ledger.TotalDebit, ledger.TotalCredit, ledger.ClosingBalance]),
            ],
            Summary: figures,
            Notes: ["Relevé établi à partir du journal du compte client (mouvements conservés, jamais modifiés)."]);
        return Result.Success(await print.RenderAsync(doc, f, "statement:customer", $"releve-{summary.CustomerNumber}", "financial", customerId, ct: ct));
    }

    /// <summary>Période par défaut du relevé ticket (jours).</summary>
    private const int TicketStatementDays = 90;

    /// <summary>
    /// Relevé au format ticket 80 mm (imprimante de caisse) : client, solde reporté, mouvements en 4 colonnes
    /// (date, opération, montant — positif = dette, négatif = versement ou avoir —, solde), puis total versé,
    /// avoir, retard et reste à verser mis en évidence.
    /// </summary>
    private static PrintDocument StatementTicket(
        CustomerAccountSummaryDto summary, CustomerLedgerDto ledger, CurrencyBalanceDto? balance, PrintParty? party,
        string cur, DateOnly? from, DateOnly? to, bool autoPeriod)
    {
        var rows = new List<IReadOnlyList<object?>>();
        if (from is not null) rows.Add([from, "Solde reporté", null, ledger.OpeningBalance]);
        rows.AddRange(ledger.Entries.Select(e => (IReadOnlyList<object?>)
            [e.OccurredAt, e.Reference is null ? e.Label : $"{e.Label} {e.Reference}", e.Debit - e.Credit, e.Balance]));

        var figures = new List<PrintFigure>();
        if (cur == summary.Currency) figures.Add(new("Versé sur la période", summary.TotalPaid, K.Money, cur));
        var store = balance?.AvailableStoreCredit ?? summary.AvailableStoreCredit;
        var overdue = balance?.OverdueAmount ?? summary.OverdueAmount;
        if (store > 0) figures.Add(new("Avoir disponible", store, K.Money, cur));
        if (overdue > 0) figures.Add(new("Dont en retard", overdue, K.Money, cur));
        figures.Add(new("Reste à verser", balance?.BalanceDue ?? summary.BalanceDue, K.Money, cur, Emphasis: true));

        var notes = new List<string>();
        if (autoPeriod) notes.Add($"{TicketStatementDays} derniers jours : les mouvements antérieurs sont repris dans le solde reporté. Relevé complet : format A4.");
        notes.Add("Montant positif = achat à crédit ou dépense ; négatif = versement, retour ou avoir.");

        return new PrintDocument(
            Title: "Relevé de compte",
            Reference: summary.CustomerNumber,
            Subtitle: Period(from, to),
            Parties: party is null ? null : [party with { Lines = party.Lines.Take(3).ToList() }],
            Details: Details(("Devise", cur, K.Text)),
            Tables:
            [
                new PrintTable(null,
                    [new("Date", K.Date), new("Opération"), new("Montant", K.Money), new("Solde", K.Money)],
                    rows),
            ],
            Summary: figures,
            Notes: notes);
    }

    private async Task<DateOnly> CompanyTodayAsync(CancellationToken ct)
    {
        var zone = await db.Database.SqlQuery<string>($"""
            SELECT timezone AS "Value" FROM nexora.company WHERE id = {Company}
            """).FirstOrDefaultAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, PrintValues.Zone(zone)).DateTime);
    }

    // ================================================================== Outils

    private sealed record PartyRow(string Number, string Name, string? Phone, string? Email, string? Address, string? TaxId);

    private async Task<PrintParty?> CustomerPartyAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Database.SqlQuery<PartyRow>($"""
            SELECT customer_number AS "Number", name AS "Name", phone AS "Phone", email AS "Email",
                   nullif(concat_ws(', ', nullif(btrim(address), ''), nullif(btrim(city), '')), '') AS "Address",
                   tax_identifier AS "TaxId"
            FROM nexora.customer WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        return c is null ? null : Party("Client", c);
    }

    private static PrintParty Party(string label, PartyRow r) => new(label,
        new[]
        {
            r.Name, $"Code : {r.Number}", r.Phone is null ? null : $"Tél. : {r.Phone}", r.Email, r.Address,
            r.TaxId is null ? null : $"NIF : {r.TaxId}",
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList());

    /// <summary>Détails d'une opération : les valeurs vides ne sont pas imprimées.</summary>
    private static List<PrintFigure> Details(params (string Label, object? Value, string Kind)[] items) =>
        items.Where(i => i.Value is not null && (i.Value is not string s || !string.IsNullOrWhiteSpace(s)))
             .Select(i => new PrintFigure(i.Label, i.Value, i.Kind))
             .ToList();

    private static string Period(DateOnly? from, DateOnly? to) => (from, to) switch
    {
        (null, null) => "Depuis l'ouverture du compte",
        ({ } a, null) => $"Depuis le {a:dd/MM/yyyy}",
        (null, { } b) => $"Jusqu'au {b:dd/MM/yyyy}",
        ({ } a, { } b) => $"Du {a:dd/MM/yyyy} au {b:dd/MM/yyyy}",
    };

    private static string SettlementLabel(string kind) => kind switch
    {
        "payment" => "Versement",
        "credit_memo" => "Avoir imputé",
        "write_off" => "Radiation",
        _ => kind,
    };

    private static string SourceLabel(string sourceType) => sourceType switch
    {
        "sale" => "Vente",
        "customer_expense" => "Dépense imputée",
        "opening" => "Reprise de solde",
        _ => sourceType,
    };

    private static string CreditStatusLabel(string status) => status switch
    {
        "open" => "Ouvert",
        "partial" => "Partiellement payé",
        "paid" => "Payé",
        "overdue" => "En retard",
        "blocked" => "En litige",
        "cancelled" => "Annulé",
        "written_off" => "Radié",
        _ => status,
    };

    private static string InstallmentLabel(string status) => status switch
    {
        "paid" => "Payée",
        "partial" => "Partielle",
        "overdue" => "En retard",
        "upcoming" => "À venir",
        _ => status,
    };

    private static string RefundMethodLabel(string method) => method switch
    {
        "cash" => "Espèces",
        "card" => "Carte",
        "bank_transfer" => "Virement",
        "mobile_money" => "Mobile money",
        "check" => "Chèque",
        _ => "Autre",
    };
}
