using Nexora.Contracts.Security;
using Nexora.Domain.Common;

namespace Nexora.Application.Security;

/// <summary>
/// Consultation du journal d'audit (Phase 11 §10-12, §39). Lecture seule, bornée au
/// périmètre entreprise du demandeur ; le journal lui-même est immuable (trigger DB).
/// </summary>
public interface IAuditQueryService
{
    Task<AuditPageDto> QueryAsync(AuditQueryFilter filter, CancellationToken ct = default);
    Task<Result<(string FileName, string Content)>> ExportAsync(AuditQueryFilter filter, CancellationToken ct = default);
}

/// <summary>
/// Moteur de détection anti-fraude (Phase 11 §14-18). Applique des règles configurables sur
/// l'activité récente et produit des <c>fraud_signal</c> notés — l'alerte ne remplace pas le
/// jugement humain (§1). Aucun signal n'est supprimé sans trace (§17).
/// </summary>
public interface IFraudDetectionService
{
    Task<FraudScanResult> ScanAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FraudSignalDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<Result> ReviewAsync(Guid signalId, ReviewSignalRequest request, CancellationToken ct = default);
}

/// <summary>
/// Workflow d'approbation des opérations sensibles (Phase 11 §19-20). Séparation des
/// responsabilités : demandeur ≠ approbateur ; double approbation selon la politique.
/// </summary>
public interface IApprovalWorkflowService
{
    Task<Result<ApprovalOutcome>> SubmitAsync(SubmitApprovalRequest request, CancellationToken ct = default);
    Task<Result<ApprovalRequestDto>> DecideAsync(Guid requestId, ApprovalDecisionRequest decision, CancellationToken ct = default);
    Task<IReadOnlyList<ApprovalRequestDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<Result<ApprovalRequestDto>> GetAsync(Guid requestId, CancellationToken ct = default);
}

/// <summary>Ré-authentification renforcée avant une action critique (Phase 11 §7, §36).</summary>
public interface IStepUpAuthService
{
    Task<Result<StepUpChallengeResponse>> ChallengeAsync(StepUpChallengeRequest request, CancellationToken ct = default);
    Task<bool> VerifyAsync(StepUpVerifyRequest request, CancellationToken ct = default);
    /// <summary>Réauthentification valide pour cette catégorie (non consommée : valable jusqu'à son expiration).</summary>
    Task<bool> ValidateAsync(string token, string purpose, Guid? sessionId, CancellationToken ct = default);
}

/// <summary>
/// Registre des sessions et révocation, dont la « déconnexion globale » (Phase 11 §6, §30). Un
/// appareil (Phase 47 §11) EST une session — <see cref="ListDevicesAsync"/> et <see cref="SetTrustedAsync"/>
/// opèrent sur le même registre, jamais un second concept.
/// </summary>
public interface ISessionSecurityService
{
    /// <summary>Enregistre une session à l'ouverture (appelé après login — le contexte tenant n'est pas encore posé).</summary>
    Task RecordAsync(Guid userId, Guid? activeCompanyId, string? deviceLabel, string? ipAddress, string? userAgent, Guid? tokenId = null, CancellationToken ct = default);
    /// <summary>Ouvre la session (identifiant porté par le jeton) et émet son premier jeton de rafraîchissement.</summary>
    Task<SessionStart> StartAsync(Guid sessionId, Guid userId, Guid? activeCompanyId, string? ipAddress, string? userAgent, Guid? tokenId, CancellationToken ct = default);
    /// <summary>Rotation : consomme le jeton de rafraîchissement, en émet un nouveau avec un nouveau jeton d'accès.
    /// Un jeton déjà consommé trahit un vol : toutes les sessions de l'utilisateur sont révoquées.</summary>
    Task<Result<RefreshOutcome>> RefreshAsync(string refreshToken, string? ipAddress, CancellationToken ct = default);
    /// <summary>Déconnexion de la session courante (jeton d'accès et jetons de rafraîchissement).</summary>
    Task RevokeSessionAsync(Guid sessionId, string reason, CancellationToken ct = default);
    /// <summary>Déconnexion par le cookie de rafraîchissement : fonctionne même si le jeton d'accès a expiré.</summary>
    Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken ct = default);
    /// <summary>Déconnexion (recette AUTH-004) : révoque la session portée par ce jeton — le
    /// middleware refuse ensuite tout appel avec ce même jeton.</summary>
    Task<Result> RevokeTokenAsync(Guid tokenId, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<UserSessionDto>> ListAsync(CancellationToken ct = default);
    Task<Result> RevokeAsync(Guid sessionId, RevokeSessionRequest request, CancellationToken ct = default);
    Task<Result> RevokeAllAsync(RevokeSessionRequest request, CancellationToken ct = default);
    Task<Result> SetTrustedAsync(Guid sessionId, bool trusted, CancellationToken ct = default);
}

/// <summary>Politique de sécurité par entreprise (Phase 11 §7, §21, §31).</summary>
public interface ISecurityPolicyService
{
    Task<SecurityPolicyDto> GetAsync(CancellationToken ct = default);
    Task<Result> SetAsync(SetSecurityPolicyRequest request, CancellationToken ct = default);
}

// =============================================================================
// Sécurité, IAM & Conformité (Phase 47) — le socle ci-dessus reste inchangé et validé.
// =============================================================================

/// <summary>
/// Chiffrement au repos du secret TOTP (Phase 47 §17-18). Le secret ne transite JAMAIS en clair
/// hors de <c>SqlMfaService</c> — ni en base, ni en log.
/// </summary>
public interface IMfaSecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

/// <summary>
/// Registre des vulnérabilités et résultats SAST/DAST/SCA/pentest (Phase 47 §42-44). Table
/// plateforme — ce sont des constats sur NEXORA ONE elle-même, jamais une donnée d'une entreprise.
/// </summary>
public interface ISecurityFindingService
{
    Task<IReadOnlyList<SecurityFindingDto>> ListAsync(string? status, string? severity, CancellationToken ct = default);
    Task<Result<SecurityFindingDto>> ReportAsync(ReportFindingRequest request, CancellationToken ct = default);
    Task<Result<SecurityFindingDto>> UpdateStatusAsync(Guid id, UpdateFindingStatusRequest request, CancellationToken ct = default);
}

/// <summary>Inventaire de certificats (Phase 47 §53). Table plateforme.</summary>
public interface ISecurityCertificateService
{
    Task<IReadOnlyList<SecurityCertificateDto>> ListAsync(CancellationToken ct = default);
    Task<Result<SecurityCertificateDto>> RegisterAsync(RegisterCertificateRequest request, CancellationToken ct = default);
    Task<Result<SecurityCertificateDto>> RotateAsync(Guid id, RotateCertificateRequest request, CancellationToken ct = default);
    /// <summary>Recalcule le statut de chaque certificat selon son échéance (job planifié §53).</summary>
    Task<int> RefreshExpiryStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// Registre de conformité auto-évalué (Phase 47 §35). Auto-évaluation honnête de la posture
/// NEXORA ONE — jamais une certification externe (SOC2/ISO exigent un auditeur tiers).
/// </summary>
public interface IComplianceRegistryService
{
    Task<IReadOnlyList<ComplianceControlDto>> ListAsync(string? domain, CancellationToken ct = default);
    Task<Result<ComplianceControlDto>> ReviewAsync(Guid id, ReviewComplianceControlRequest request, CancellationToken ct = default);
}

/// <summary>
/// Accès d'urgence « break-glass » (Phase 47 §12). Réutilise intégralement le moteur JIT de la
/// Phase 22 (<c>UserAssignmentService.GrantAsync</c>/<c>RevokeAsync</c>) — cette interface ne porte
/// que la demande et sa décision, jamais un second mécanisme d'octroi de permission. Jamais
/// auto-approuvé, même par un super-administrateur (§6).
/// </summary>
public interface IEmergencyAccessService
{
    Task<IReadOnlyList<EmergencyAccessGrantDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<Result<EmergencyAccessGrantDto>> RequestAsync(RequestEmergencyAccessRequest request, CancellationToken ct = default);
    Task<Result<EmergencyAccessGrantDto>> DecideAsync(Guid id, DecideEmergencyAccessRequest decision, CancellationToken ct = default);
    Task<Result> RevokeAsync(Guid id, string reason, CancellationToken ct = default);
}

/// <summary>
/// Score & tableau de bord de sécurité (Phase 47 §56-57) — agrège les signaux déjà produits par
/// les autres services de sécurité (MFA, sessions, fraude, approbations, vulnérabilités,
/// certificats, incidents), jamais un nouveau stockage : calculé à la lecture.
/// </summary>
public interface ISecurityScoreService
{
    Task<SecurityScoreDto> ComputeAsync(CancellationToken ct = default);
    Task<SecurityDashboardDto> DashboardAsync(CancellationToken ct = default);
}

/// <summary>Session ouverte : jeton de rafraîchissement (en clair, remis une seule fois au client) et fin de session.</summary>
public sealed record SessionStart(string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Résultat d'un rafraîchissement : nouveau jeton d'accès et nouveau jeton de rafraîchissement.</summary>
public sealed record RefreshOutcome(Nexora.Contracts.Auth.AuthTokenResponse Token, string RefreshToken, DateTimeOffset ExpiresAt);
