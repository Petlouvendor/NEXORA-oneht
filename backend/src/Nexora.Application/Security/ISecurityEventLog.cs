namespace Nexora.Application.Security;

/// <summary>
/// Journalisation explicite des évènements de sécurité (Phase 33 §33-34, §46) — connexions,
/// MFA, verrouillages, révocations. Écrit dans <c>audit_log</c> (immuable, trigger DB) et
/// survit à la suppression du compte. Complète l'audit automatique par trigger des tables métier.
/// </summary>
public interface ISecurityEventLog
{
    /// <summary>
    /// Enregistre un évènement. <paramref name="action"/> suit la nomenclature <c>login.succeeded</c>,
    /// <c>login.failed</c>, <c>login.locked</c>, <c>mfa.enrolled</c>, <c>mfa.disabled</c>,
    /// <c>mfa.challenge_failed</c>, <c>mfa.reset_by_admin</c>… <paramref name="context"/> est
    /// sérialisé en jsonb (jamais de secret, jamais de mot de passe).
    /// </summary>
    Task RecordAsync(
        string action,
        Guid? userId,
        Guid? companyId,
        string? detail = null,
        string? ipAddress = null,
        object? context = null,
        CancellationToken ct = default);
}
