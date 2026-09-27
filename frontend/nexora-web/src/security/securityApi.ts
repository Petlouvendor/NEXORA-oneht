import { apiFetch } from "@/core/apiClient";

export interface AuditEvent {
  id: string;
  occurredAt: string;
  userId: string | null;
  companyId: string | null;
  action: string;
  entityType: string;
  entityId: string | null;
  changes: Record<string, string> | null;
  ipAddress: string | null;
  deviceId: string | null;
}

export interface AuditPage {
  events: AuditEvent[];
  total: number;
  page: number;
  pageSize: number;
}

export interface FraudSignal {
  id: string;
  ruleCode: string;
  ruleName: string;
  category: string;
  severity: string;
  riskScore: number;
  subjectType: string;
  subjectLabel: string | null;
  actorUserId: string | null;
  observedValue: number;
  threshold: number;
  detail: string;
  status: string;
  detectedAt: string;
  reviewNote: string | null;
}

export interface ApprovalAction {
  approverId: string | null;
  level: number;
  action: string;
  comment: string | null;
  at: string;
}

export interface ApprovalRequest {
  id: string;
  operationType: string;
  amount: number | null;
  requestedBy: string | null;
  status: string;
  currentLevel: number;
  requiredLevels: number;
  requestedAt: string;
  actions: ApprovalAction[];
}

export interface UserSession {
  id: string;
  activeCompanyId: string | null;
  deviceLabel: string | null;
  ipAddress: string | null;
  issuedAt: string;
  lastSeenAt: string;
  revokedAt: string | null;
  revokeReason: string | null;
  current: boolean;
  deviceType: string;
  trusted: boolean;
}

export const queryAudit = (params: Record<string, string>) => {
  const q = new URLSearchParams(params).toString();
  return apiFetch<AuditPage>(`/security/audit${q ? `?${q}` : ""}`);
};

export const scanFraud = () =>
  apiFetch<{ rulesEvaluated: number; newSignals: number; updatedSignals: number }>("/security/fraud/scan", { method: "POST" });

export const listSignals = (status?: string) =>
  apiFetch<FraudSignal[]>(`/security/fraud/signals${status ? `?status=${status}` : ""}`);

export const reviewSignal = (id: string, status: string, note?: string) =>
  apiFetch<void>(`/security/fraud/signals/${id}/review`, { method: "POST", body: JSON.stringify({ status, note: note ?? null }) });

export const listApprovals = (status?: string) =>
  apiFetch<ApprovalRequest[]>(`/security/approvals${status ? `?status=${status}` : ""}`);

export const decideApproval = (id: string, approve: boolean, note?: string) =>
  apiFetch<ApprovalRequest>(`/security/approvals/${id}/decide`, { method: "POST", body: JSON.stringify({ approve, note: note ?? null }) });

export const listSessions = () => apiFetch<UserSession[]>("/security/sessions");

export const revokeSession = (id: string, reason: string) =>
  apiFetch<void>(`/security/sessions/${id}/revoke`, { method: "POST", body: JSON.stringify({ reason }) });

export const revokeAllSessions = (reason: string) =>
  apiFetch<void>("/security/sessions/revoke-all", { method: "POST", body: JSON.stringify({ reason }) });

// --- Authentification multi-facteur (Phase 33 §8-9) ---------------------------

export interface MfaStatus {
  enabled: boolean;
  enrolledAt: string | null;
  recoveryCodesRemaining: number;
  policyRequires: boolean;
}

export interface MfaEnrollment {
  secret: string;
  otpauthUri: string;
  issuer: string;
  account: string;
}

export const getMfaStatus = () => apiFetch<MfaStatus>("/auth/mfa");

export const beginMfaEnrollment = () => apiFetch<MfaEnrollment>("/auth/mfa/enroll", { method: "POST" });

export const confirmMfaEnrollment = (code: string) =>
  apiFetch<{ recoveryCodes: string[] }>("/auth/mfa/enroll/confirm", {
    method: "POST",
    body: JSON.stringify({ code }),
  });

export const disableMfa = (code: string) =>
  apiFetch<void>("/auth/mfa/disable", { method: "POST", body: JSON.stringify({ code }) });

export const resetUserMfa = (userId: string) =>
  apiFetch<void>(`/users/${userId}/mfa/reset`, { method: "POST" });

// --- Appareils (Phase 47 §11) — un appareil EST une session, même registre ----

export const trustDevice = (sessionId: string, trusted: boolean) =>
  apiFetch<void>(`/security/devices/${sessionId}/trust`, { method: "POST", body: JSON.stringify({ trusted }) });

// --- Accès d'urgence « break-glass » (Phase 47 §12) ---------------------------

export interface EmergencyAccessGrant {
  id: string;
  requestedBy: string;
  roleName: string;
  scopeType: string;
  establishmentId: string | null;
  reason: string;
  requestedDurationHours: number;
  status: string;
  requestedAt: string;
  decidedBy: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
  revokedAt: string | null;
  expiresAt: string | null;
  isActive: boolean;
}

export const listEmergencyAccess = (status?: string) =>
  apiFetch<EmergencyAccessGrant[]>(`/security/emergency-access${status ? `?status=${status}` : ""}`);

export const requestEmergencyAccess = (roleName: string, reason: string, requestedDurationHours: number) =>
  apiFetch<EmergencyAccessGrant>("/security/emergency-access", {
    method: "POST",
    body: JSON.stringify({ roleName, reason, requestedDurationHours }),
  });

export const decideEmergencyAccess = (id: string, approve: boolean, note?: string) =>
  apiFetch<EmergencyAccessGrant>(`/security/emergency-access/${id}/decide`, {
    method: "POST", body: JSON.stringify({ approve, note: note ?? null }),
  });

export const revokeEmergencyAccess = (id: string, reason: string) =>
  apiFetch<void>(`/security/emergency-access/${id}/revoke`, { method: "POST", body: JSON.stringify({ reason }) });

// --- Vulnérabilités, certificats & conformité (Phase 47 §35, §42-44, §53) — personnel plateforme --

export interface SecurityFinding {
  id: string;
  source: string;
  severity: string;
  title: string;
  description: string | null;
  cveId: string | null;
  component: string | null;
  status: string;
  slaDueAt: string | null;
  discoveredAt: string;
  resolvedAt: string | null;
  remediationNotes: string | null;
}

export const listFindings = (status?: string, severity?: string) => {
  const q = new URLSearchParams({ ...(status ? { status } : {}), ...(severity ? { severity } : {}) }).toString();
  return apiFetch<SecurityFinding[]>(`/security/vulnerabilities${q ? `?${q}` : ""}`);
};

export const reportFinding = (input: {
  source: string; severity: string; title: string; description?: string; cveId?: string; component?: string;
}) => apiFetch<SecurityFinding>("/security/vulnerabilities", { method: "POST", body: JSON.stringify(input) });

export const updateFindingStatus = (id: string, status: string, remediationNotes?: string) =>
  apiFetch<SecurityFinding>(`/security/vulnerabilities/${id}/status`, {
    method: "POST", body: JSON.stringify({ status, remediationNotes: remediationNotes ?? null }),
  });

export interface SecurityCertificate {
  id: string;
  name: string;
  usageType: string;
  issuer: string | null;
  fingerprint: string | null;
  notBefore: string | null;
  notAfter: string;
  status: string;
  rotatedTo: string | null;
  notes: string | null;
}

export const listCertificates = () => apiFetch<SecurityCertificate[]>("/security/certificates");

export const registerCertificate = (input: {
  name: string; usageType: string; issuer?: string; fingerprint?: string; notBefore?: string; notAfter: string; notes?: string;
}) => apiFetch<SecurityCertificate>("/security/certificates", { method: "POST", body: JSON.stringify(input) });

export const rotateCertificate = (id: string, input: { name: string; notBefore?: string; notAfter: string; fingerprint?: string }) =>
  apiFetch<SecurityCertificate>(`/security/certificates/${id}/rotate`, { method: "POST", body: JSON.stringify(input) });

export interface ComplianceControl {
  id: string;
  code: string;
  domain: string;
  title: string;
  description: string | null;
  status: string;
  evidenceRef: string | null;
  reviewedBy: string | null;
  reviewedAt: string | null;
  reviewNotes: string | null;
}

export const listComplianceControls = (domain?: string) =>
  apiFetch<ComplianceControl[]>(`/security/compliance${domain ? `?domain=${domain}` : ""}`);

export const reviewComplianceControl = (id: string, status: string, evidenceRef?: string, reviewNotes?: string) =>
  apiFetch<ComplianceControl>(`/security/compliance/${id}/review`, {
    method: "POST", body: JSON.stringify({ status, evidenceRef: evidenceRef ?? null, reviewNotes: reviewNotes ?? null }),
  });

// --- Score & tableau de bord de sécurité (Phase 47 §56-57) --------------------

export interface SecurityScoreFactor {
  key: string;
  label: string;
  score: number;
  maxScore: number;
  explanation: string;
}

export interface SecurityScore {
  score: number;
  grade: string;
  factors: SecurityScoreFactor[];
  computedAt: string;
}

export interface SecurityDashboard {
  score: SecurityScore;
  activeSessions: number;
  trustedDevices: number;
  openFraudSignals: number;
  pendingApprovals: number;
  openFindingsCritical: number;
  openFindingsHigh: number;
  certificatesExpiringSoon: number;
  openSecurityIncidents: number;
  pendingEmergencyAccessRequests: number;
  mfaEnrolledUsers: number;
  totalUsers: number;
}

export const getSecurityScore = () => apiFetch<SecurityScore>("/security/score");

export const getSecurityDashboard = () => apiFetch<SecurityDashboard>("/security/dashboard");
