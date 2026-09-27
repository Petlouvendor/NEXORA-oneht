namespace Nexora.Contracts.Security;

// --- Journal d'audit (§10-12, §39) -----------------------------------

public sealed record AuditQueryFilter(
    DateOnly? From = null, DateOnly? To = null, string? Action = null,
    string? EntityType = null, Guid? EntityId = null, Guid? UserId = null,
    int Page = 1, int PageSize = 50);

public sealed record AuditEventDto(
    Guid Id, DateTimeOffset OccurredAt, Guid? UserId, Guid? CompanyId,
    string Action, string EntityType, Guid? EntityId,
    IReadOnlyDictionary<string, string>? Changes, string? IpAddress, string? DeviceId);

public sealed record AuditPageDto(IReadOnlyList<AuditEventDto> Events, long Total, int Page, int PageSize);

// --- Anti-fraude (§14-18, §38) --------------------------------------

public sealed record FraudScanResult(int RulesEvaluated, int NewSignals, int UpdatedSignals);

public sealed record FraudSignalDto(
    Guid Id, string RuleCode, string RuleName, string Category, string Severity, int RiskScore,
    string SubjectType, Guid? SubjectId, string? SubjectLabel, Guid? ActorUserId,
    DateTimeOffset WindowFrom, DateTimeOffset WindowTo, decimal ObservedValue, decimal Threshold,
    string Detail, string Status, DateTimeOffset DetectedAt, string? ReviewNote);

public sealed record ReviewSignalRequest(string Status, string? Note);   // reviewing | confirmed | false_positive | dismissed

// --- Approbations (§19-20, §40) ------------------------------------

public sealed record SubmitApprovalRequest(
    string OperationType, string? ReferenceType, Guid? ReferenceId, decimal? Amount, string? Reason);

public sealed record ApprovalOutcome(
    bool ApprovalRequired, Guid? RequestId, string Status, int CurrentLevel, int RequiredLevels, string? Message);

public sealed record ApprovalRequestDto(
    Guid Id, string OperationType, string? ReferenceType, Guid? ReferenceId, decimal? Amount,
    Guid? RequestedBy, string Status, int CurrentLevel, int RequiredLevels,
    DateTimeOffset RequestedAt, IReadOnlyList<ApprovalActionDto> Actions);

public sealed record ApprovalActionDto(Guid? ApproverId, int Level, string Action, string? Comment, DateTimeOffset At);

public sealed record ApprovalDecisionRequest(bool Approve, string? Note);

// --- Ré-authentification renforcée (§7) ----------------------------

public sealed record StepUpChallengeRequest(string Password, string Purpose);
public sealed record StepUpChallengeResponse(string Token, string Purpose, DateTimeOffset ExpiresAt);
public sealed record StepUpVerifyRequest(string Token, string Purpose);

// --- Sessions (§6, §30) ------------------------------------------

public sealed record UserSessionDto(
    Guid Id, Guid? ActiveCompanyId, string? DeviceLabel, string? IpAddress,
    DateTimeOffset IssuedAt, DateTimeOffset LastSeenAt, DateTimeOffset? RevokedAt, string? RevokeReason, bool Current,
    string DeviceType = "unknown", bool Trusted = false);

public sealed record RevokeSessionRequest(string Reason);

/// <summary>Un appareil (Phase 47 §11) EST une session — même registre, jamais un second concept.</summary>
public sealed record SetDeviceTrustedRequest(bool Trusted);

// --- Politique de sécurité (§7, §21, §31) ------------------------

public sealed record SecurityPolicyDto(
    Guid CompanyId, IReadOnlyList<string> MfaRequiredRoles, int SessionTtlMinutes, int StepUpTtlMinutes,
    bool SensitiveExportRequiresApproval, bool ExportWatermark, int AuditRetentionDays);

public sealed record SetSecurityPolicyRequest(
    IReadOnlyList<string>? MfaRequiredRoles, int? SessionTtlMinutes, int? StepUpTtlMinutes,
    bool? SensitiveExportRequiresApproval, bool? ExportWatermark, int? AuditRetentionDays);

// =============================================================================
// Sécurité, IAM & Conformité (Phase 47) — vulnérabilités, certificats, conformité auto-évaluée,
// accès d'urgence, incidents de sécurité, score & tableau de bord.
// =============================================================================

// --- Vulnérabilités & résultats SAST/DAST/SCA/pentest (§42-44) ----------------

public sealed record SecurityFindingDto(
    Guid Id, string Source, string Severity, string Title, string? Description, string? CveId, string? Component,
    string Status, DateTimeOffset? SlaDueAt, DateTimeOffset DiscoveredAt, DateTimeOffset? ResolvedAt,
    string? RemediationNotes);

public sealed record ReportFindingRequest(
    string Source, string Severity, string Title, string? Description = null, string? CveId = null, string? Component = null);

public sealed record UpdateFindingStatusRequest(string Status, string? RemediationNotes = null);

// --- Certificats (§53) ---------------------------------------------------------

public sealed record SecurityCertificateDto(
    Guid Id, string Name, string UsageType, string? Issuer, string? Fingerprint,
    DateOnly? NotBefore, DateOnly NotAfter, string Status, Guid? RotatedTo, string? Notes);

public sealed record RegisterCertificateRequest(
    string Name, string UsageType, string? Issuer, string? Fingerprint, DateOnly? NotBefore, DateOnly NotAfter, string? Notes = null);

public sealed record RotateCertificateRequest(string Name, DateOnly? NotBefore, DateOnly NotAfter, string? Fingerprint);

// --- Conformité auto-évaluée (§35) ---------------------------------------------

public sealed record ComplianceControlDto(
    Guid Id, string Code, string Domain, string Title, string? Description, string Status,
    string? EvidenceRef, Guid? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNotes);

public sealed record ReviewComplianceControlRequest(string Status, string? EvidenceRef, string? ReviewNotes);

// --- Accès d'urgence « break-glass » (§12) -------------------------------------

public sealed record EmergencyAccessGrantDto(
    Guid Id, Guid RequestedBy, string RoleName, string ScopeType, Guid? EstablishmentId, string Reason,
    int RequestedDurationHours, string Status, DateTimeOffset RequestedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt,
    string? DecisionNote, DateTimeOffset? RevokedAt, DateTimeOffset? ExpiresAt, bool IsActive);

public sealed record RequestEmergencyAccessRequest(
    string RoleName, string Reason, int RequestedDurationHours, string ScopeType = "COMPANY", Guid? EstablishmentId = null);

public sealed record DecideEmergencyAccessRequest(bool Approve, string? Note);

// --- Score & tableau de bord de sécurité (§56-57) ------------------------------

public sealed record SecurityScoreFactorDto(string Key, string Label, int Score, int MaxScore, string Explanation);

public sealed record SecurityScoreDto(int Score, string Grade, IReadOnlyList<SecurityScoreFactorDto> Factors, DateTimeOffset ComputedAt);

public sealed record SecurityDashboardDto(
    SecurityScoreDto Score, int ActiveSessions, int TrustedDevices, int OpenFraudSignals,
    int PendingApprovals, int OpenFindingsCritical, int OpenFindingsHigh, int CertificatesExpiringSoon,
    int OpenSecurityIncidents, int PendingEmergencyAccessRequests, int MfaEnrolledUsers, int TotalUsers);
