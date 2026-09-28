using Microsoft.EntityFrameworkCore;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;
using Nexora.Domain.Finance;

namespace Nexora.Infrastructure.Persistence;

public sealed partial class NexoraDbContext
{
    partial void ConfigureFinance(ModelBuilder b)
    {
        var lower = new Func<string, string>(s => s.ToLowerInvariant());

        b.Entity<Currency>(e =>
        {
            e.ToTable("currency");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Symbol).HasColumnName("symbol");
            e.Property(x => x.DecimalPlaces).HasColumnName("decimal_places");
            e.Property(x => x.Status).HasColumnName("status");
        });

        b.Entity<Account>(e =>
        {
            e.ToTable("account");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ChartOfAccountsId).HasColumnName("chart_of_accounts_id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.AccountType).HasColumnName("account_type")
                .HasConversion(v => lower(v.ToString()), s => Enum.Parse<AccountType>(s, true));
            e.Property(x => x.ParentAccountId).HasColumnName("parent_account_id");
            e.Property(x => x.IsPostable).HasColumnName("is_postable");
            e.Property(x => x.CurrencyId).HasColumnName("currency_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<AccountMapping>(e =>
        {
            e.ToTable("account_mapping");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.MappingKey).HasColumnName("mapping_key");
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<Journal>(e =>
        {
            e.ToTable("journal");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.JournalType).HasColumnName("journal_type");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<AccountingPeriod>(e =>
        {
            e.ToTable("accounting_period");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.StartDate).HasColumnName("start_date");
            e.Property(x => x.EndDate).HasColumnName("end_date");
            e.Property(x => x.Status).HasColumnName("status")
                .HasConversion(v => lower(v.ToString()), s => Enum.Parse<PeriodStatus>(s, true));
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.ClosedBy).HasColumnName("closed_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<JournalEntry>(e =>
        {
            e.ToTable("journal_entry");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.JournalId).HasColumnName("journal_id");
            e.Property(x => x.AccountingPeriodId).HasColumnName("accounting_period_id");
            e.Property(x => x.EntryNumber).HasColumnName("entry_number");
            e.Property(x => x.EntryDate).HasColumnName("entry_date");
            e.Property(x => x.Origin).HasColumnName("origin");
            e.Property(x => x.SourceType).HasColumnName("source_type");
            e.Property(x => x.SourceId).HasColumnName("source_id");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Status).HasColumnName("status")
                .HasConversion(v => lower(v.ToString()), s => Enum.Parse<JournalEntryStatus>(s, true));
            e.Property(x => x.PostedAt).HasColumnName("posted_at");
            e.Property(x => x.PostedBy).HasColumnName("posted_by");
            e.Property(x => x.ReversalOfEntryId).HasColumnName("reversal_of_entry_id");
            e.Property(x => x.IsIntercompany).HasColumnName("is_intercompany");
            e.Property(x => x.SourceOperationUuid).HasColumnName("source_operation_uuid");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JournalEntryId);
        });

        b.Entity<JournalEntryLine>(e =>
        {
            e.ToTable("journal_entry_line");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.JournalEntryId).HasColumnName("journal_entry_id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.LineNumber).HasColumnName("line_number");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Debit).HasColumnName("debit");
            e.Property(x => x.Credit).HasColumnName("credit");
            e.Property(x => x.CurrencyId).HasColumnName("currency_id");
            e.Property(x => x.ExchangeRateId).HasColumnName("exchange_rate_id");
            e.Property(x => x.BaseDebit).HasColumnName("base_debit");
            e.Property(x => x.BaseCredit).HasColumnName("base_credit");
            e.Property(x => x.ThirdPartyType).HasColumnName("third_party_type");
            e.Property(x => x.ThirdPartyId).HasColumnName("third_party_id");
            e.Property(x => x.CostCenterId).HasColumnName("cost_center_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("payment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.PaymentNumber).HasColumnName("payment_number");
            e.Property(x => x.Direction).HasColumnName("direction")
                .HasConversion(v => lower(v.ToString()), s => Enum.Parse<PaymentDirection>(s, true));
            e.Property(x => x.PartyType).HasColumnName("party_type");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.SupplierId).HasColumnName("supplier_id");
            e.Property(x => x.PaymentMethodId).HasColumnName("payment_method_id");
            e.Property(x => x.FinancialAccountId).HasColumnName("financial_account_id");
            e.Property(x => x.CashSessionId).HasColumnName("cash_session_id");
            e.Property(x => x.CurrencyId).HasColumnName("currency_id");
            e.Property(x => x.ExchangeRateId).HasColumnName("exchange_rate_id");
            e.Property(x => x.Amount).HasColumnName("amount");
            e.Property(x => x.AllocatedAmount).HasColumnName("allocated_amount").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.Reference).HasColumnName("reference");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at");
            e.Property(x => x.SourceOperationUuid).HasColumnName("source_operation_uuid");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.Note).HasColumnName("note");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
            e.Ignore(x => x.Allocations);
        });

        b.Entity<PaymentAllocation>(e =>
        {
            e.ToTable("payment_allocation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.PaymentId).HasColumnName("payment_id");
            e.Property(x => x.ReceivableId).HasColumnName("receivable_id");
            e.Property(x => x.PayableId).HasColumnName("payable_id");
            e.Property(x => x.AllocatedAmount).HasColumnName("allocated_amount");
            e.Property(x => x.FxDifference).HasColumnName("fx_difference");
            e.Property(x => x.CurrencyId).HasColumnName("currency_id");
            e.Property(x => x.AllocatedAt).HasColumnName("allocated_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
        });

        b.Entity<CustomerReceivable>(e =>
        {
            e.ToTable("customer_receivable");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.SaleId).HasColumnName("sale_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.CurrencyId).HasColumnName("currency_id");
            e.Property(x => x.OriginalAmount).HasColumnName("original_amount");
            e.Property(x => x.PaidAmount).HasColumnName("paid_amount").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.IssueDate).HasColumnName("issue_date");
            e.Property(x => x.DueDate).HasColumnName("due_date");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.SourceType).HasColumnName("source_type");
            e.Property(x => x.SourceId).HasColumnName("source_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<SupplierPayable>(e =>
        {
            e.ToTable("supplier_payable");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.SupplierInvoiceId).HasColumnName("supplier_invoice_id");
            e.Property(x => x.SupplierId).HasColumnName("supplier_id");
            e.Property(x => x.CurrencyId).HasColumnName("currency_id");
            e.Property(x => x.OriginalAmount).HasColumnName("original_amount");
            e.Property(x => x.PaidAmount).HasColumnName("paid_amount").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.CreditedAmount).HasColumnName("credited_amount").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.IssueDate).HasColumnName("issue_date");
            e.Property(x => x.DueDate).HasColumnName("due_date");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<FinancialAccount>(e =>
        {
            e.ToTable("financial_account");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.AccountType).HasColumnName("account_type");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.AccountNumberMasked).HasColumnName("account_number_masked");
            e.Property(x => x.CurrencyId).HasColumnName("currency_id");
            e.Property(x => x.OpeningBalance).HasColumnName("opening_balance");
            e.Property(x => x.CurrentBalance).HasColumnName("current_balance");
            e.Property(x => x.LedgerAccountId).HasColumnName("ledger_account_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<FinancialTransaction>(e =>
        {
            e.ToTable("financial_transaction");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.FinancialAccountId).HasColumnName("financial_account_id");
            e.Property(x => x.TransactionType).HasColumnName("transaction_type");
            e.Property(x => x.Amount).HasColumnName("amount");
            e.Property(x => x.CurrencyId).HasColumnName("currency_id");
            e.Property(x => x.ExchangeRateId).HasColumnName("exchange_rate_id");
            e.Property(x => x.CounterpartyAccountId).HasColumnName("counterparty_account_id");
            e.Property(x => x.ReferenceType).HasColumnName("reference_type");
            e.Property(x => x.ReferenceId).HasColumnName("reference_id");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.SourceOperationUuid).HasColumnName("source_operation_uuid");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<ExchangeRate>(e =>
        {
            e.ToTable("exchange_rate");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.BaseCurrencyId).HasColumnName("base_currency_id");
            e.Property(x => x.QuoteCurrencyId).HasColumnName("quote_currency_id");
            e.Property(x => x.Rate).HasColumnName("rate");
            e.Property(x => x.EffectiveAt).HasColumnName("effective_at");
            e.Property(x => x.Source).HasColumnName("source");
        });
    }
}
