using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance;
using Nexora.Contracts.Finance;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Finance;

/// <inheritdoc cref="IOpeningBalanceRecovery"/>
public sealed class OpeningBalanceRecoveryService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IFinanceOperations ops) : IOpeningBalanceRecovery
{
    private sealed record Row(
        Guid CompanyId, string CompanyName, Guid? GroupId, Guid AccountId, string AccountName, string Currency,
        decimal Amount, DateTimeOffset OpeningAt);

    public async Task<IReadOnlyList<OpeningBalanceRecoveryLine>> RunAsync(bool apply, Guid? accountId = null,
        CancellationToken ct = default)
    {
        List<Row> rows;
        using (tenant.BeginSystemScope())
            // DatÃ© juste avant le premier mouvement (ou Ã  la crÃ©ation du compte) : l'ouverture prÃ©cÃ¨de tout le reste.
            rows = await db.Database.SqlQuery<Row>($"""
                SELECT co.id AS "CompanyId", co.name AS "CompanyName", co.group_id AS "GroupId",
                       fa.id AS "AccountId", fa.name AS "AccountName", cur.code AS "Currency",
                       fa.opening_balance AS "Amount",
                       LEAST(fa.created_at, COALESCE((SELECT min(tx.occurred_at) FROM nexora.financial_transaction tx
                                                      WHERE tx.financial_account_id = fa.id) - interval '1 second',
                                                     fa.created_at)) AS "OpeningAt"
                FROM nexora.financial_account fa
                JOIN nexora.company co ON co.id = fa.company_id
                JOIN nexora.currency cur ON cur.id = fa.currency_id
                WHERE fa.opening_balance <> 0 AND ({accountId}::uuid IS NULL OR fa.id = {accountId})
                ORDER BY co.name, fa.name
                """).ToListAsync(ct);

        var lines = new List<OpeningBalanceRecoveryLine>();
        foreach (var r in rows)
        {
            var line = new OpeningBalanceRecoveryLine(r.CompanyId, r.CompanyName, r.AccountId, r.AccountName, r.Currency,
                r.Amount, r.OpeningAt, "a_reprendre");
            if (!apply) { lines.Add(line); continue; }

            tenant.SetScope([r.CompanyId], r.GroupId is { } g ? [g] : [], r.CompanyId);
            try
            {
                var posted = await uow.ExecuteInTransactionAsync(async token =>
                {
                    // Le moteur ajoute la transaction Â« opening Â» au solde ; la colonne, dÃ©jÃ  comptÃ©e dans ce solde,
                    // est ensuite retirÃ©e : le solde affichÃ© reste le mÃªme (ordre choisi pour qu'une caisse ne passe
                    // jamais, mÃªme un instant, sous zÃ©ro).
                    var result = await ops.RecordTreasuryOpeningAsync(new RecordTreasuryOpeningRequest(
                        OperationFor(r.AccountId), r.OpeningAt, r.AccountId, r.Amount, r.Currency), token);
                    if (!result.IsSuccess) throw new RecoveryRefused(result.Error!.Message);
                    var moved = await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.financial_account
                        SET current_balance = current_balance - {r.Amount}, opening_balance = 0, updated_at = now()
                        WHERE id = {r.AccountId} AND opening_balance = {r.Amount}
                        """, token);
                    if (moved != 1) throw new RecoveryRefused("Le compte a changÃ© pendant la reprise.");
                    return result.Value!;
                }, ct);
                lines.Add(line with { Status = "repris", EntryNumber = posted.EntryNumber });
            }
            catch (Exception ex) when (ex is RecoveryRefused or InvalidOperationException)
            {
                db.ChangeTracker.Clear();
                lines.Add(line with { Status = "refuse", Detail = ex.Message });
            }
        }
        return lines;
    }

    /// <summary>Identifiant d'opÃ©ration stable par compte : une reprise relancÃ©e est reconnue comme un rejeu.</summary>
    private static Guid OperationFor(Guid accountId)
        => new(MD5.HashData(Encoding.UTF8.GetBytes($"nexora.opening-recovery:{accountId}")));

    private sealed class RecoveryRefused(string message) : Exception(message);
}
