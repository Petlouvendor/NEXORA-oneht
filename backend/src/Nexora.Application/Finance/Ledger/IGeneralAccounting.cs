using Nexora.Contracts.Finance;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance.Ledger;

/// <summary>
/// Workflow comptable manuel (Phase 29 §9-14, §47). Complète le moteur événementiel
/// (Phase 4) : ce que le comptable saisit à la main dans le journal des opérations diverses.
/// La contrepassation reste centralisée dans <c>IReversalService</c>.
/// </summary>
public interface IManualJournalService
{
    Task<IReadOnlyList<JournalEntryDto>> ListAsync(string? status, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<IReadOnlyList<JournalEntryDto>> SearchAsync(string? status, DateOnly? from, DateOnly? to, string? journal, string? q, int limit,
        CancellationToken ct = default);
    Task<Result<JournalEntryDto>> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Crée un brouillon (numéro provisoire, pas d'exigence d'équilibre — §11).</summary>
    Task<Result<Guid>> CreateDraftAsync(CreateJournalEntryRequest r, CancellationToken ct = default);

    /// <summary>Remplace les lignes d'un brouillon (§11).</summary>
    Task<Result> UpdateDraftAsync(Guid id, CreateJournalEntryRequest r, CancellationToken ct = default);

    /// <summary>Supprime un brouillon (jamais une écriture validée — §2, §11).</summary>
    Task<Result> DeleteDraftAsync(Guid id, CancellationToken ct = default);

    /// <summary>Passe les contrôles §47 puis valide : numéro définitif, période, auteur (§10, §12).</summary>
    Task<Result<PostJournalEntryResult>> PostAsync(Guid id, CancellationToken ct = default);

    /// <summary>Contrepassation d'une écriture validée (§13) — délègue à <c>IReversalService</c>.</summary>
    Task<Result<Guid>> ReverseAsync(Guid id, ReverseJournalEntryRequest r, CancellationToken ct = default);
}

/// <summary>Batterie de contrôles comptables §47 — pré-validation lisible avant les gardes SQL.</summary>
public interface IAccountingControls
{
    Task<IReadOnlyList<AccountingControlDto>> EvaluateAsync(CreateJournalEntryRequest r, CancellationToken ct = default);
}

/// <summary>Lectures du plan comptable & des journaux (§4-8, §51).</summary>
public interface IChartQueries
{
    Task<IReadOnlyList<ChartOfAccountsDto>> ChartsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AccountDto>> AccountsAsync(string? accountType, bool onlyPostable, CancellationToken ct = default);
    Task<IReadOnlyList<JournalDto>> JournalsAsync(CancellationToken ct = default);
}

/// <summary>
/// Clôture d'exercice & à-nouveaux (Phase 29 §16, §18-19). N'écrit que des écritures
/// comptables tracées (journaux CL / AN) — jamais de suppression, jamais de solde parallèle.
/// </summary>
public interface IFiscalYearService
{
    Task<Result<YearEndCloseReportDto>> PreviewCloseAsync(Guid periodId, CancellationToken ct = default);

    /// <summary>Solde les comptes de résultat vers le résultat de l'exercice (journal CL), puis clôture la période.</summary>
    Task<Result<CloseYearResultDto>> CloseYearAsync(Guid periodId, CloseYearRequest r, CancellationToken ct = default);

    /// <summary>
    /// Réouverture d'un exercice clôturé (audit 2026-10 éd. 2, A-044) : annule l'écriture de clôture par son miroir,
    /// passe la clôture à « reopened » avec le motif et rouvre la période. Renvoie faux si l'exercice n'avait pas de
    /// clôture d'exercice (simple réouverture de période, à la charge de l'appelant).
    /// </summary>
    Task<Result<bool>> ReopenClosedYearAsync(Guid periodId, string reason, CancellationToken ct = default);

    /// <summary>Génère l'écriture d'à-nouveaux d'une période à partir des soldes de bilan de la période source (journal AN).</summary>
    Task<Result<OpeningBalancesResultDto>> GenerateOpeningBalancesAsync(Guid targetPeriodId, GenerateOpeningBalancesRequest r, CancellationToken ct = default);
}
