namespace Nexora.Contracts.Printing;

// Impression & export : un seul modèle de document, rendu en vrai PDF (A4 ou ticket 80 mm),
// en vrai classeur Excel (.xlsx) ou en CSV. Les reçus sont construits par le serveur depuis la
// base ; l'export d'un écran reçoit le tableau affiché (document non contractuel).

/// <summary>Formats de sortie : <c>pdf</c> (A4), <c>ticket</c> (PDF 80 mm), <c>xlsx</c>, <c>csv</c>.</summary>
public static class PrintFormats
{
    public const string Pdf = "pdf";
    public const string Ticket = "ticket";
    public const string Xlsx = "xlsx";
    public const string Csv = "csv";

    public static readonly IReadOnlyList<string> All = [Pdf, Ticket, Xlsx, Csv];

    public static string Normalize(string? format) => (format ?? Pdf).Trim().ToLowerInvariant() switch
    {
        "excel" or "xls" => Xlsx,
        "a4" => Pdf,
        "80mm" => Ticket,
        var f => f,
    };

    public static bool IsValid(string format) => All.Contains(format);
}

/// <summary>Nature d'une valeur : <c>text</c>, <c>money</c>, <c>number</c>, <c>integer</c>, <c>date</c>,
/// <c>datetime</c>, <c>percent</c>. Une cellule garde son type dans Excel (nombre, date).</summary>
public static class PrintKinds
{
    public const string Text = "text";
    public const string Money = "money";
    public const string Number = "number";
    public const string Integer = "integer";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string Percent = "percent";
}

public sealed record PrintColumn(string Label, string Kind = PrintKinds.Text, string? Currency = null);

/// <summary>Tableau : lignes de valeurs brutes (nombre, texte, date ISO) dans l'ordre des colonnes ;
/// <c>Totals</c> = ligne de totaux facultative (null pour une cellule vide).</summary>
public sealed record PrintTable(
    string? Title, IReadOnlyList<PrintColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows,
    IReadOnlyList<object?>? Totals = null);

/// <summary>Couple libellé / valeur (date, mode de paiement, total versé, reste à verser…).</summary>
public sealed record PrintFigure(string Label, object? Value, string Kind = PrintKinds.Text, string? Currency = null, bool Emphasis = false);

/// <summary>Bloc d'identité (client, fournisseur) : un libellé et quelques lignes.</summary>
public sealed record PrintParty(string Label, IReadOnlyList<string> Lines);

/// <summary>
/// Document imprimable. <c>Details</c> = informations de l'opération ; <c>Summary</c> = chiffres clés
/// encadrés (ex. total versé, reste à verser) ; <c>Stamp</c> = mention forte (ANNULÉ, DUPLICATA) ;
/// <c>Signatures</c> = zones de signature ; <c>Official</c> = document émis par le serveur (reçu) —
/// faux pour l'export d'un écran, qui porte alors la mention « non contractuel ».
/// </summary>
public sealed record PrintDocument(
    string Title,
    string? Reference = null,
    string? Subtitle = null,
    IReadOnlyList<PrintParty>? Parties = null,
    IReadOnlyList<PrintFigure>? Details = null,
    IReadOnlyList<PrintTable>? Tables = null,
    IReadOnlyList<PrintFigure>? Summary = null,
    IReadOnlyList<string>? Notes = null,
    IReadOnlyList<string>? Signatures = null,
    string? Stamp = null,
    bool Landscape = false,
    bool Official = true);

/// <summary>Pièce imprimable d'un tiers (paiement, réception, facture, commande…) : type, numéro, date, montant.</summary>
public sealed record PrintableItemDto(
    string Kind, Guid Id, string Number, DateTimeOffset Date, decimal? Amount, string? Currency, string Status, string? Detail);

/// <summary>Export d'un tableau affiché à l'écran (§ export de « n'importe quel rapport »).</summary>
public sealed record ScreenExportRequest(string ScreenKey, PrintDocument Document);

/// <summary>Types de reçus et documents imprimables depuis la base.</summary>
public static class PrintableKinds
{
    public const string CustomerPayment = "customer-payment";
    public const string CustomerCredit = "customer-credit";
    public const string CreditMemo = "credit-memo";
    public const string CustomerRefund = "customer-refund";
    public const string CustomerExpense = "customer-expense";
    public const string Sale = "sale";
    public const string SupplierPayment = "supplier-payment";
    public const string SupplierInvoice = "supplier-invoice";
    public const string PurchaseOrder = "purchase-order";
    public const string GoodsReceipt = "goods-receipt";
    // Phase 4 : devis, commande client, document commercial émis (facture, avoir, bon de livraison…).
    public const string Quote = "quote";
    public const string SalesOrder = "sales-order";
    public const string IssuedDocument = "document";

    public static readonly IReadOnlyList<string> All =
    [
        CustomerPayment, CustomerCredit, CreditMemo, CustomerRefund, CustomerExpense, Sale,
        SupplierPayment, SupplierInvoice, PurchaseOrder, GoodsReceipt, Quote, SalesOrder, IssuedDocument,
    ];
}
