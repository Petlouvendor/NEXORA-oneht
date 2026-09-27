using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Finance;
using Nexora.Contracts.Finance;
using Nexora.Domain.Common;
using Nexora.Domain.Sales;

namespace Nexora.Application.Sales;

/// <summary>
/// Sessions de caisse (Phase 6 §25-27). THÉORIQUE = OUVERTURE + ENTRÉES − SORTIES.
/// Guide fonctionnel §13 : un caissier n'agit que sur SA session (superviseur : <c>pos.manage_sessions</c>) ;
/// un écart au-delà de <c>pos.cash_variance_threshold</c> laisse la session « closing » jusqu'à la
/// validation d'un superviseur distinct ; une clôture finalisée est figée (trigger en base).
/// </summary>
public sealed class CashSessionService(
    ICashSessionStore store,
    IFinanceOperations finance,
    IPermissionService permissions,
    ISettingsService settings,
    IPosApprovalNotifier notifier,
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    public async Task<Result<Guid>> OpenAsync(OpenSessionCommand cmd, CancellationToken ct = default)
    {
        var register = await store.GetRegisterAsync(cmd.CashRegisterId, ct);
        if (register is null) return Error.NotFound("pos.register_not_found", "Caisse introuvable.");
        if (cmd.OpeningBalance < 0m) return Error.Validation("pos.opening_balance", "Le fond de caisse ne peut pas être négatif.");
        if (await store.GetOpenSessionAsync(cmd.CashRegisterId, ct) is not null)
            return Error.Conflict("pos.session_already_open", "Une session est déjà ouverte sur cette caisse.");

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            CashRegisterId = cmd.CashRegisterId,
            PosTerminalId = cmd.PosTerminalId,
            UserId = tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis."),
            OpenedAt = clock.UtcNow,
            OpeningBalance = cmd.OpeningBalance,
            Status = "open",
        };
        await store.AddSessionAsync(session, ct);
        await uow.SaveChangesAsync(ct);   // la session doit exister avant tout mouvement
        if (cmd.OpeningBalance > 0m)
        {
            await store.AddMovementAsync(new CashMovement
            {
                Id = Guid.NewGuid(), CompanyId = Company, CashSessionId = session.Id,
                MovementType = "opening_float", Amount = cmd.OpeningBalance, CurrencyId = register.CurrencyId,
                OccurredAt = clock.UtcNow, UserId = tenant.UserId, CreatedAt = clock.UtcNow,
            }, ct);
            await uow.SaveChangesAsync(ct);
        }
        return Result.Success(session.Id);
    }

    public async Task<Result> CashInOutAsync(CashInOutCommand cmd, CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(cmd.SessionId, ct);
        if (session is null || session.Status != "open")
            return Error.Conflict("pos.session_closed", "Session close ou introuvable.");
        if (await OwnershipRefusalAsync(session, ct) is { } refused) return refused;
        if (cmd.MovementType is not ("payin" or "payout" or "pickup"))
            return Error.Validation("pos.movement_type", "Type de mouvement invalide.");
        // cash_movement_amount_chk : un mouvement de caisse n'est jamais nul.
        if (cmd.Amount == 0m)
            return Error.Validation("pos.movement_amount", "Le montant doit être différent de zéro.");
        var signed = cmd.MovementType == "payin" ? Math.Abs(cmd.Amount) : -Math.Abs(cmd.Amount);
        await store.AddMovementAsync(new CashMovement
        {
            Id = Guid.NewGuid(), CompanyId = Company, CashSessionId = cmd.SessionId,
            MovementType = cmd.MovementType, Amount = signed,
            CurrencyId = (await store.GetRegisterAsync(session.CashRegisterId, ct))!.CurrencyId,
            Note = cmd.Note, OccurredAt = clock.UtcNow, UserId = tenant.UserId, CreatedAt = clock.UtcNow,
        }, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Clôture : le montant compté est saisi, l'attendu et l'écart sont calculés par le serveur (§13).
    /// Écart dans le seuil → session close, écart passé en comptabilité. Au-delà → « closing » :
    /// plus aucune vente, un superviseur est notifié et doit valider (<see cref="ValidateCloseAsync"/>).
    /// </summary>
    public async Task<Result<SessionSummary>> CloseAsync(CloseSessionCommand cmd, CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(cmd.SessionId, ct);
        if (session is null) return Error.NotFound("pos.session_not_found", "Session introuvable.");
        if (session.Status == "closing")
            return Error.Conflict("pos.session_pending_validation", "Clôture déjà comptée : en attente de validation Manager.");
        if (!session.IsOpen) return Error.Conflict("pos.session_closed", "Session déjà close.");
        if (await OwnershipRefusalAsync(session, ct) is { } refused) return refused;
        if (cmd.CountedBalance < 0m) return Error.Validation("pos.counted_balance", "Le montant compté ne peut pas être négatif.");

        var register = (await store.GetRegisterAsync(session.CashRegisterId, ct))!;
        var (saleCount, cashIn, cashOut) = await store.SessionTotalsAsync(cmd.SessionId, ct);
        var expected = session.OpeningBalance + cashIn + cashOut;
        var difference = cmd.CountedBalance - expected;

        var threshold = await VarianceThresholdAsync(ct);
        if (threshold is { } t && Math.Abs(difference) > t)
        {
            await store.MarkCountedAsync(cmd.SessionId, expected, cmd.CountedBalance, cmd.Notes, ct);
            var currency = await store.CurrencyCodeAsync(register.CurrencyId, ct);
            await notifier.NotifyApproversAsync(Security.Permissions.Pos.ManageSessions, "Écart de caisse", register.Name,
                $"écart de {difference:0.00} {currency} à la clôture (attendu {expected:0.00}, compté {cmd.CountedBalance:0.00}), au-delà du seuil de {t:0.00}",
                $"/pos?approvals=closings&id={cmd.SessionId}", "cash_session", cmd.SessionId, ct);
            return Result.Success(new SessionSummary(
                cmd.SessionId, "closing", session.OpeningBalance, expected, cmd.CountedBalance, difference, saleCount, cashIn));
        }

        return await FinalizeAsync(session, register, expected, cmd.CountedBalance, cmd.Notes, null, null, saleCount, cashIn, ct);
    }

    /// <summary>Validation Manager d'une clôture en écart (§13) : jamais par le caissier de la session ;
    /// motif obligatoire ; passe l'écriture d'écart et fige la session.</summary>
    public async Task<Result<SessionSummary>> ValidateCloseAsync(ValidateCloseCommand cmd, CancellationToken ct = default)
    {
        if (!(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Security.Permissions.Pos.ManageSessions))
            return Error.Forbidden("pos.manage_sessions_forbidden", "Valider une clôture exige la permission pos.manage_sessions.");
        if (string.IsNullOrWhiteSpace(cmd.Note))
            return Error.Validation("pos.decision_note_required", "Indiquez le motif de validation de l'écart.");
        var session = await store.GetSessionAsync(cmd.SessionId, ct);
        if (session is null || session.CompanyId != Company) return Error.NotFound("pos.session_not_found", "Session introuvable.");
        if (session.Status != "closing")
            return Error.Conflict("pos.session_not_pending", "Cette session n'attend pas de validation.");
        if (session.UserId == tenant.UserId)
            return Error.Forbidden("pos.self_approval", "Vous ne pouvez pas valider la clôture de votre propre caisse.");

        var register = (await store.GetRegisterAsync(session.CashRegisterId, ct))!;
        var (saleCount, cashIn, _) = await store.SessionTotalsAsync(cmd.SessionId, ct);
        var summary = await FinalizeAsync(session, register, session.ExpectedBalance ?? 0m, session.CountedBalance ?? 0m,
            session.Notes, tenant.UserId, cmd.Note.Trim(), saleCount, cashIn, ct);
        if (summary.IsSuccess)
            await notifier.NotifyRequesterAsync(session.UserId, "Clôture de caisse", register.Name, true, cmd.Note.Trim(),
                "/pos", "cash_session", session.Id, ct);
        return summary;
    }

    private async Task<Result<SessionSummary>> FinalizeAsync(CashSession session, CashRegister register, decimal expected,
        decimal counted, string? notes, Guid? validatedBy, string? validationNote, int saleCount, decimal cashIn, CancellationToken ct)
    {
        var difference = counted - expected;
        var currency = await store.CurrencyCodeAsync(register.CurrencyId, ct);
        return await uow.ExecuteInTransactionAsync<Result<SessionSummary>>(async token =>
        {
            await store.FinalizeCloseAsync(session.Id, expected, counted, notes, validatedBy, validationNote, token);
            // Sortie du tiroir à la clôture. Attendu nul (sans fond de caisse ni vente en espèces) :
            // rien ne sort — et un mouvement de caisse n'est jamais nul (cash_movement_amount_chk).
            if (expected != 0m)
                await store.AddMovementAsync(new CashMovement
                {
                    Id = Guid.NewGuid(), CompanyId = Company, CashSessionId = session.Id,
                    MovementType = "closing", Amount = -expected, CurrencyId = register.CurrencyId,
                    OccurredAt = clock.UtcNow, UserId = tenant.UserId, CreatedAt = clock.UtcNow,
                }, token);

            // Écart de caisse → moteur financier (§17 Phase 4), dans la devise de la caisse.
            if (difference != 0m && register.FinancialAccountId is { } fa)
            {
                var post = await finance.RecordCashCountAsync(new RecordCashCountRequest(
                    Guid.NewGuid(), clock.UtcNow, session.Id, fa, expected, counted, currency));
                if (!post.IsSuccess) throw new InvalidOperationException(post.Error!.Message);
            }

            return Result.Success(new SessionSummary(
                session.Id, "closed", session.OpeningBalance, expected, counted, difference, saleCount, cashIn));
        }, ct);
    }

    /// <summary>Guide §13 : « les opérations des autres caisses ne sont pas visibles par défaut ».</summary>
    private async Task<Error?> OwnershipRefusalAsync(CashSession session, CancellationToken ct)
    {
        if (session.CompanyId != Company) return Error.NotFound("pos.session_not_found", "Session introuvable.");
        if (session.UserId == tenant.UserId) return null;
        return (await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Security.Permissions.Pos.ManageSessions)
            ? null
            : Error.Forbidden("pos.session_not_owned", "Cette session de caisse n'est pas la vôtre.");
    }

    private async Task<decimal?> VarianceThresholdAsync(CancellationToken ct)
    {
        try
        {
            var effective = await settings.ResolveAsync("pos.cash_variance_threshold", null, ct);
            return effective.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? effective.Value.GetDecimal() : null;
        }
        catch (InvalidOperationException)
        {
            return null;   // réglage absent : pas de validation d'écart
        }
    }
}
