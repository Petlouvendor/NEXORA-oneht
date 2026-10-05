using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Security;
using Nexora.Contracts.Security;

namespace Nexora.Api.Controllers;

/// <summary>
/// Sécurité, audit &amp; anti-fraude (Phase 11). Consultation du journal d'audit (immuable),
/// revue des signaux anti-fraude, workflow d'approbation, ré-authentification renforcée et
/// gestion des sessions. Tout est contrôlé côté serveur et borné au périmètre du demandeur.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/security")]
public sealed class SecurityController(
    IAuditQueryService audit,
    IFraudDetectionService fraud,
    IApprovalWorkflowService approvals,
    IStepUpAuthService stepUp,
    ISessionSecurityService sessions,
    ISecurityPolicyService policy,
    ISecurityFindingService findings,
    ISecurityCertificateService certificates,
    IComplianceRegistryService compliance,
    IEmergencyAccessService emergencyAccess,
    ISecurityScoreService score) : ApiControllerBase
{
    // --- Journal d'audit (§10-12, §39) ---------------------------

    [HttpGet("audit")]
    [HttpGet("~/api/v{version:apiVersion}/ops/audit")]
    [RequirePermission(Permissions.Security.AuditView)]
    public async Task<IActionResult> Audit([FromQuery] AuditQueryFilter filter, CancellationToken ct)
        => Ok(await audit.QueryAsync(filter, ct));

    [HttpPost("audit/export")]
    [RequirePermission(Permissions.Security.AuditView)]
    public async Task<IActionResult> ExportAudit([FromBody] AuditQueryFilter filter, CancellationToken ct)
    {
        var r = await audit.ExportAsync(filter, ct);
        if (!r.IsSuccess) return FromResult(r);
        return File(Encoding.UTF8.GetBytes(r.Value.Content), "text/csv", r.Value.FileName);
    }

    // --- Anti-fraude (§14-18, §38) ------------------------------

    [HttpPost("fraud/scan")]
    [RequirePermission(Permissions.Security.FraudReview)]
    public async Task<IActionResult> Scan(CancellationToken ct)
        => Ok(await fraud.ScanAsync(ct));

    [HttpGet("fraud/signals")]
    [RequirePermission(Permissions.Security.FraudReview)]
    public async Task<IActionResult> Signals([FromQuery] string? status, CancellationToken ct)
        => Ok(await fraud.ListAsync(status, ct));

    [HttpPost("fraud/signals/{signalId:guid}/review")]
    [RequirePermission(Permissions.Security.FraudReview)]
    public async Task<IActionResult> ReviewSignal(Guid signalId, [FromBody] ReviewSignalRequest r, CancellationToken ct)
        => FromResult(await fraud.ReviewAsync(signalId, r, ct));

    // --- Approbations (§19-20) ---------------------------------

    [HttpPost("approvals")]
    [RequirePermission(Permissions.Security.Approvals)]
    public async Task<IActionResult> Submit([FromBody] SubmitApprovalRequest r, CancellationToken ct)
        => FromResult(await approvals.SubmitAsync(r, ct));

    [HttpGet("approvals")]
    [RequirePermission(Permissions.Security.Approvals)]
    public async Task<IActionResult> ListApprovals([FromQuery] string? status, CancellationToken ct)
        => Ok(await approvals.ListAsync(status, ct));

    [HttpGet("approvals/{requestId:guid}")]
    [RequirePermission(Permissions.Security.Approvals)]
    public async Task<IActionResult> GetApproval(Guid requestId, CancellationToken ct)
        => FromResult(await approvals.GetAsync(requestId, ct));

    [HttpPost("approvals/{requestId:guid}/decide")]
    [RequirePermission(Permissions.Security.Approvals)]
    public async Task<IActionResult> Decide(Guid requestId, [FromBody] ApprovalDecisionRequest r, CancellationToken ct)
        => FromResult(await approvals.DecideAsync(requestId, r, ct));

    // --- Ré-authentification renforcée (§7) -------------------

    [HttpPost("step-up")]
    // Limite par adresse plus large que la connexion (bureau partagé) ; le forçage est bloqué par le verrouillage
    // par utilisateur (5 échecs en 15 minutes).
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("refresh")]
    public async Task<IActionResult> StepUp([FromBody] StepUpChallengeRequest r, CancellationToken ct)
        => FromResult(await stepUp.ChallengeAsync(r with
        {
            SessionId = Guid.TryParse(User.FindFirst("sid")?.Value, out var sid) ? sid : null,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }, ct));

    [HttpPost("step-up/verify")]
    public async Task<IActionResult> StepUpVerify([FromBody] StepUpVerifyRequest r, CancellationToken ct)
        => Ok(new { verified = await stepUp.VerifyAsync(r, ct) });

    // --- Sessions (§6, §30) ---------------------------------

    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions(CancellationToken ct)
        => Ok(await sessions.ListAsync(ct));

    [HttpPost("sessions/{sessionId:guid}/revoke")]
    public async Task<IActionResult> RevokeSession(Guid sessionId, [FromBody] RevokeSessionRequest r, CancellationToken ct)
        => FromResult(await sessions.RevokeAsync(sessionId, r, ct));

    [HttpPost("sessions/revoke-all")]
    public async Task<IActionResult> RevokeAllSessions([FromBody] RevokeSessionRequest r, CancellationToken ct)
        => FromResult(await sessions.RevokeAllAsync(r, ct));

    // --- Appareils (§11) — un appareil EST une session, même registre --------

    [HttpGet("devices")]
    public async Task<IActionResult> Devices(CancellationToken ct) => Ok(await sessions.ListAsync(ct));

    [HttpPost("devices/{sessionId:guid}/revoke")]
    public async Task<IActionResult> RevokeDevice(Guid sessionId, [FromBody] RevokeSessionRequest r, CancellationToken ct)
        => FromResult(await sessions.RevokeAsync(sessionId, r, ct));

    [HttpPost("devices/{sessionId:guid}/trust")]
    public async Task<IActionResult> TrustDevice(Guid sessionId, [FromBody] SetDeviceTrustedRequest r, CancellationToken ct)
        => FromResult(await sessions.SetTrustedAsync(sessionId, r.Trusted, ct));

    // --- Politique de sécurité (§7, §21, §31) ---------------

    [HttpGet("policy")]
    [RequirePermission(Permissions.Security.Configure)]
    public async Task<IActionResult> GetPolicy(CancellationToken ct)
        => Ok(await policy.GetAsync(ct));

    [HttpPut("policy")]
    [RequirePermission(Permissions.Security.Configure)]
    public async Task<IActionResult> SetPolicy([FromBody] SetSecurityPolicyRequest r, CancellationToken ct)
        => FromResult(await policy.SetAsync(r, ct));

    // --- Vulnérabilités & résultats SAST/DAST/SCA/pentest (§42-44) — réservé au personnel plateforme ---

    [HttpGet("vulnerabilities")]
    [RequirePermission(Permissions.Platform.Security)]
    public async Task<IActionResult> Findings([FromQuery] string? status, [FromQuery] string? severity, CancellationToken ct)
        => Ok(await findings.ListAsync(status, severity, ct));

    [HttpPost("vulnerabilities")]
    [RequirePermission(Permissions.Platform.Security)]
    public async Task<IActionResult> ReportFinding([FromBody] ReportFindingRequest r, CancellationToken ct)
        => FromResult(await findings.ReportAsync(r, ct));

    [HttpPost("vulnerabilities/{id:guid}/status")]
    [RequirePermission(Permissions.Platform.Security)]
    public async Task<IActionResult> UpdateFindingStatus(Guid id, [FromBody] UpdateFindingStatusRequest r, CancellationToken ct)
        => FromResult(await findings.UpdateStatusAsync(id, r, ct));

    // --- Certificats (§53) — réservé au personnel plateforme ------------------

    [HttpGet("certificates")]
    [RequirePermission(Permissions.Platform.Security)]
    public async Task<IActionResult> Certificates(CancellationToken ct) => Ok(await certificates.ListAsync(ct));

    [HttpPost("certificates")]
    [RequirePermission(Permissions.Platform.Security)]
    public async Task<IActionResult> RegisterCertificate([FromBody] RegisterCertificateRequest r, CancellationToken ct)
        => FromResult(await certificates.RegisterAsync(r, ct));

    [HttpPost("certificates/{id:guid}/rotate")]
    [RequirePermission(Permissions.Platform.Security)]
    public async Task<IActionResult> RotateCertificate(Guid id, [FromBody] RotateCertificateRequest r, CancellationToken ct)
        => FromResult(await certificates.RotateAsync(id, r, ct));

    // --- Conformité auto-évaluée (§35) — réservé au personnel plateforme ------

    [HttpGet("compliance")]
    [RequirePermission(Permissions.Platform.Security)]
    public async Task<IActionResult> Compliance([FromQuery] string? domain, CancellationToken ct)
        => Ok(await compliance.ListAsync(domain, ct));

    [HttpPost("compliance/{id:guid}/review")]
    [RequirePermission(Permissions.Platform.Security)]
    public async Task<IActionResult> ReviewCompliance(Guid id, [FromBody] ReviewComplianceControlRequest r, CancellationToken ct)
        => FromResult(await compliance.ReviewAsync(id, r, ct));

    // --- Accès d'urgence « break-glass » (§12) --------------------------------

    [HttpGet("emergency-access")]
    [RequirePermission(Permissions.Security.RequestEmergencyAccess)]
    public async Task<IActionResult> EmergencyAccess([FromQuery] string? status, CancellationToken ct)
        => Ok(await emergencyAccess.ListAsync(status, ct));

    [HttpPost("emergency-access")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Security.RequestEmergencyAccess)]
    public async Task<IActionResult> RequestEmergencyAccess([FromBody] RequestEmergencyAccessRequest r, CancellationToken ct)
        => FromResult(await emergencyAccess.RequestAsync(r, ct));

    [HttpPost("emergency-access/{id:guid}/decide")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Security.ApproveEmergencyAccess)]
    public async Task<IActionResult> DecideEmergencyAccess(Guid id, [FromBody] DecideEmergencyAccessRequest r, CancellationToken ct)
        => FromResult(await emergencyAccess.DecideAsync(id, r, ct));

    [HttpPost("emergency-access/{id:guid}/revoke")]
    [RequirePermission(Permissions.Security.ApproveEmergencyAccess)]
    public async Task<IActionResult> RevokeEmergencyAccess(Guid id, [FromBody] RevokeSessionRequest r, CancellationToken ct)
        => FromResult(await emergencyAccess.RevokeAsync(id, r.Reason, ct));

    // --- Score & tableau de bord (§56-57) --------------------------------------

    [HttpGet("score")]
    [RequirePermission(Permissions.Security.Configure)]
    public async Task<IActionResult> Score(CancellationToken ct) => Ok(await score.ComputeAsync(ct));

    [HttpGet("dashboard")]
    [RequirePermission(Permissions.Security.Configure)]
    public async Task<IActionResult> Dashboard(CancellationToken ct) => Ok(await score.DashboardAsync(ct));
}
