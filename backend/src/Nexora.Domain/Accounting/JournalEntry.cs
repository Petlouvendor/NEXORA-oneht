using Nexora.Domain.Common;

namespace Nexora.Domain.Accounting;

public enum JournalEntryStatus { Draft, Posted, Reversed, Cancelled }

public sealed class Journal : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string JournalType { get; set; } = "general";
    public string Status { get; set; } = "active";
}

public enum PeriodStatus { Open, Closed, Locked }

public sealed class AccountingPeriod : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public PeriodStatus Status { get; set; } = PeriodStatus.Open;
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }

    public bool Contains(DateOnly date) => date >= StartDate && date <= EndDate;
    public bool IsOpen => Status == PeriodStatus.Open;
}

/// <summary>Écriture comptable — table nexora.journal_entry (Phase 4 §7).</summary>
public sealed class JournalEntry : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid JournalId { get; set; }
    public Guid AccountingPeriodId { get; set; }
    public string EntryNumber { get; set; } = string.Empty;
    public DateOnly EntryDate { get; set; }
    /// <summary>Origine : system / manual / opening / closing / reversal / adjustment (Phase 29 §9, §19).</summary>
    public string Origin { get; set; } = "system";
    public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }
    public string? Description { get; set; }
    public JournalEntryStatus Status { get; set; } = JournalEntryStatus.Draft;
    public DateTimeOffset? PostedAt { get; set; }
    public Guid? PostedBy { get; set; }
    public Guid? ReversalOfEntryId { get; set; }
    public bool IsIntercompany { get; set; }
    public Guid? SourceOperationUuid { get; set; }
    public Guid? CreatedBy { get; set; }

    public List<JournalEntryLine> Lines { get; set; } = [];
}

/// <summary>Ligne d'écriture — table nexora.journal_entry_line.</summary>
public sealed class JournalEntryLine : AuditableEntity
{
    public Guid JournalEntryId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid AccountId { get; set; }
    public int LineNumber { get; set; }
    public string? Description { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public Guid CurrencyId { get; set; }
    public Guid? ExchangeRateId { get; set; }
    public decimal BaseDebit { get; set; }
    public decimal BaseCredit { get; set; }
    public string? ThirdPartyType { get; set; }
    public Guid? ThirdPartyId { get; set; }
    public Guid? CostCenterId { get; set; }
}
