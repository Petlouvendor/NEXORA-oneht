using Nexora.Application.Security;
using Nexora.Contracts.Printing;
using Nexora.Domain.Common;

namespace Nexora.Application.Printing;

/// <summary>Fichier produit : vrai PDF, vrai classeur Excel (.xlsx) ou CSV.</summary>
public sealed record RenderedFile(string FileName, string ContentType, byte[] Content);

/// <summary>En-tête de l'entreprise émettrice, repris sur chaque document.</summary>
public sealed record PrintIssuer(
    string Name, string? LegalName, string? TaxId, string? Address, string? Phone, string? Footer,
    string? AccentColor, string TimeZone);

/// <summary>Qui imprime et quand (pied de page « Imprimé le … par … »).</summary>
public sealed record PrintMeta(DateTimeOffset PrintedAt, string? PrintedBy);

/// <summary>Rendu pur (sans base) d'un <see cref="PrintDocument"/> dans un format donné.</summary>
public interface IDocumentRenderer
{
    RenderedFile Render(PrintDocument document, PrintIssuer issuer, PrintMeta meta, string format, string fileBaseName);
}

/// <summary>Rendu + en-tête de l'entreprise active + journalisation (export_log) + comptage des réimpressions.</summary>
public interface IPrintService
{
    /// <param name="logKey">Clé journalisée (ex. <c>receipt:customer-payment</c>, <c>screen:credits</c>).</param>
    /// <param name="sourceId">Pièce imprimée : permet de marquer les réimpressions « DUPLICATA ».</param>
    /// <param name="establishmentId">Établissement de l'opération (adresse et téléphone de l'en-tête) ;
    /// à défaut, le premier établissement actif de l'entreprise.</param>
    Task<RenderedFile> RenderAsync(
        PrintDocument document, string format, string logKey, string fileBaseName,
        string sensitivity = "normal", Guid? sourceId = null, Guid? establishmentId = null, CancellationToken ct = default);

    /// <summary>Nombre d'impressions déjà faites de cette pièce (0 = original).</summary>
    Task<int> PreviousPrintsAsync(string logKey, Guid sourceId, CancellationToken ct = default);
}

/// <summary>Reçus et documents imprimables construits depuis la base, droits vérifiés par type.</summary>
public interface IReceiptService
{
    /// <summary>Construit, rend et journalise un reçu (<see cref="PrintableKinds"/>). Introuvable ou hors
    /// périmètre → NotFound ; droit manquant → Forbidden.</summary>
    Task<Result<RenderedFile>> PrintAsync(string kind, Guid id, string format, CancellationToken ct = default);

    /// <summary>Relevé / situation de compte client (tous formats, Excel compris).</summary>
    Task<Result<RenderedFile>> CustomerStatementAsync(
        Guid customerId, DateOnly? from, DateOnly? to, string? currency, string format, CancellationToken ct = default);

    /// <summary>Pièces imprimables d'un fournisseur (les plus récentes d'abord), limitées aux types que
    /// l'utilisateur a le droit d'imprimer.</summary>
    Task<IReadOnlyList<PrintableItemDto>> SupplierDocumentsAsync(Guid supplierId, CancellationToken ct = default);

    /// <summary>Relevé fournisseur : factures, paiements, reste à payer.</summary>
    Task<Result<RenderedFile>> SupplierStatementAsync(
        Guid supplierId, DateOnly? from, DateOnly? to, string format, CancellationToken ct = default);
}

/// <summary>
/// Qui peut imprimer quoi. <c>Full</c> : n'importe quelle pièce du type ; <c>OwnOnly</c> : seulement les
/// pièces que l'utilisateur a lui-même enregistrées (module clients §19 : le caissier n'accède qu'aux
/// données nécessaires à son opération — le reçu de SON versement, avec le reste à verser du client).
/// </summary>
public static class PrintAccess
{
    public sealed record Rule(IReadOnlyList<string> Full, IReadOnlyList<string> OwnOnly);

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        [PrintableKinds.CustomerPayment] = new([Permissions.Credit.ViewPortfolio], [Permissions.Credit.ReceivePayment]),
        [PrintableKinds.CustomerCredit] = new([Permissions.Credit.ViewPortfolio], []),
        [PrintableKinds.CreditMemo] = new([Permissions.Credit.ViewPortfolio, Permissions.Credit.Refund], []),
        [PrintableKinds.CustomerRefund] = new([Permissions.Credit.ViewPortfolio, Permissions.Credit.Refund], []),
        [PrintableKinds.CustomerExpense] = new([Permissions.Credit.ViewPortfolio], [Permissions.Customers.CreateExpense]),
        [PrintableKinds.Sale] = new([Permissions.Pos.View], []),
        [PrintableKinds.SupplierPayment] = new([Permissions.Purchasing.ViewPayables, Permissions.Purchasing.Pay], []),
        [PrintableKinds.SupplierInvoice] = new([Permissions.Purchasing.ViewPayables, Permissions.Purchasing.Invoice], []),
        [PrintableKinds.PurchaseOrder] = new([Permissions.Purchasing.View, Permissions.Purchasing.Order], []),
        [PrintableKinds.GoodsReceipt] = new([Permissions.Purchasing.View, Permissions.Purchasing.Receive], []),
        ["customer-statement"] = new([Permissions.Credit.ViewPortfolio], []),
        ["supplier-statement"] = new([Permissions.Purchasing.ViewPayables], []),
    };

    public static Rule? For(string kind) => Rules.TryGetValue(kind, out var r) ? r : null;

    /// <summary><c>full</c>, <c>own</c> ou <c>none</c> selon les permissions détenues.</summary>
    public static string Level(string kind, Func<string, bool> has)
    {
        if (For(kind) is not { } rule) return "none";
        if (rule.Full.Any(has)) return "full";
        return rule.OwnOnly.Any(has) ? "own" : "none";
    }
}
