-- =============================================================================
-- NEXORA — V060 — Sécurité, IAM & Conformité (Phase 47)
--
-- Le socle IAM/sécurité existe déjà et reste VALIDÉ par cette phase, pas reconstruit :
--   Phase 11 (V016/V030) : RBAC (`role`/`permission`/`user_role`/`access_scope` — 7 niveaux de
--     scope PLATFORM/GROUP/COMPANY/ESTABLISHMENT/STORE/WAREHOUSE/POS_TERMINAL), `audit_log`
--     immuable, moteur anti-fraude, workflow d'approbation générique (requester≠approver),
--     step-up, `user_session` (révocation, déconnexion globale), `security_policy` par entreprise
--   Phase 22 (V035) : affectations de rôle temporaires/révocables (`user_role.valid_from/
--     valid_until/reason`) — le mécanisme JIT/JEA du §47, déjà construit
--   Phase 33 (V046) : MFA TOTP + codes de récupération, verrouillage progressif après échecs
--   Phase 35 (V048) : `api_key` — secret jamais stocké en clair (SHA-256), rotation, scopes bornés
--   Phase 38 (V051) : `support_access_grant` (accès support client temporaire, consenti, audité)
--   Phase 36 (V049) : `continuity_incident` (sévérité/runbook/post-mortem) — réutilisé, pas dupliqué
--
-- Cette migration ajoute ce qui manquait réellement : un registre de vulnérabilités/résultats
-- SAST-DAST-SCA-pentest (§42-44), un inventaire de certificats (§53), un registre de conformité
-- auto-évalué et honnête (§35), un accès d'urgence « break-glass » qui RÉUTILISE le moteur JIT de
-- la Phase 22 plutôt que d'en inventer un second (§12), l'extension du registre d'incidents
-- existant à un second domaine « sécurité » sans dupliquer le concept (§29-30), et l'enrichissement
-- des sessions existantes en un véritable registre d'appareils (§11).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Vulnérabilités & résultats SAST/DAST/SCA/pentest (§42-44) =========
-- Table plateforme (comme `platform_plan`, `job_definition`) : ce sont des constats sur le code et
-- l'infrastructure de NEXORA ONE elle-même, jamais une donnée d'une entreprise cliente.
CREATE TABLE nexora.security_finding (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    source              text        NOT NULL,
    severity            text        NOT NULL,
    title               text        NOT NULL,
    description         text,
    cve_id              text,
    component           text,
    status              text        NOT NULL DEFAULT 'open',
    sla_due_at          timestamptz,
    discovered_at       timestamptz NOT NULL DEFAULT now(),
    resolved_at         timestamptz,
    resolved_by         uuid        REFERENCES nexora.app_user(id),
    remediation_notes   text,
    reported_by         uuid        REFERENCES nexora.app_user(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT security_finding_source_chk CHECK (source IN ('sast','dast','sca','dependency','pentest','manual')),
    CONSTRAINT security_finding_severity_chk CHECK (severity IN ('critical','high','medium','low','info')),
    CONSTRAINT security_finding_status_chk CHECK (status IN
        ('open','triaged','remediating','resolved','accepted_risk','false_positive'))
);
CREATE INDEX security_finding_status_idx ON nexora.security_finding (status, severity);

-- ===================== 2. Inventaire de certificats (§53) — table plateforme =================
CREATE TABLE nexora.security_certificate (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name          text        NOT NULL,
    usage_type    text        NOT NULL DEFAULT 'tls',
    issuer        text,
    fingerprint   text,
    not_before    date,
    not_after     date        NOT NULL,
    status        text        NOT NULL DEFAULT 'active',
    rotated_to    uuid        REFERENCES nexora.security_certificate(id),
    notes         text,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT security_certificate_usage_chk CHECK (usage_type IN ('tls','signing','webhook','other')),
    CONSTRAINT security_certificate_status_chk CHECK (status IN
        ('active','expiring_soon','expired','revoked','rotated'))
);
CREATE INDEX security_certificate_expiry_idx ON nexora.security_certificate (not_after);

-- ===================== 3. Registre de conformité auto-évalué (§35) — table plateforme ========
-- Auto-évaluation honnête de la posture NEXORA ONE elle-même — jamais une certification externe
-- (SOC2/ISO exigent un auditeur tiers, hors périmètre applicatif). Amorcé avec l'état réel constaté
-- du code au moment de cette phase, y compris les manques.
CREATE TABLE nexora.compliance_control (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code            text        NOT NULL,
    domain          text        NOT NULL,
    title           text        NOT NULL,
    description     text,
    status          text        NOT NULL DEFAULT 'not_assessed',
    evidence_ref    text,
    reviewed_by     uuid        REFERENCES nexora.app_user(id),
    reviewed_at     timestamptz,
    review_notes    text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT compliance_control_code_uk UNIQUE (code),
    CONSTRAINT compliance_control_domain_chk CHECK (domain IN
        ('iam','encryption','audit','privacy','backup_dr','api_security','payment','vendor')),
    CONSTRAINT compliance_control_status_chk CHECK (status IN
        ('met','partial','not_met','not_applicable','not_assessed'))
);

INSERT INTO nexora.compliance_control (code, domain, title, description, status, evidence_ref) VALUES
    ('iam-rbac', 'iam', 'RBAC multi-niveaux avec permissions granulaires',
     'Rôles/permissions/scopes à 7 niveaux (plateforme→terminal POS), deny-by-default.',
     'met', 'V004, IPermissionService'),
    ('iam-mfa', 'iam', 'MFA disponible et applicable aux rôles sensibles',
     'TOTP + 10 codes de récupération opérationnels. La politique par rôle sensible existe mais ' ||
     'reste consultative (MfaEnrollmentRequired signalé au client, jamais bloqué côté serveur).',
     'partial', 'V046, SqlMfaService.PolicyRequiresMfaAsync'),
    ('iam-jit', 'iam', 'Accès temporaire et révocable (JIT/JEA)',
     'Affectations de rôle bornées dans le temps, motif obligatoire, anti-élévation de privilège.',
     'met', 'V035, UserAssignmentService.GrantAsync'),
    ('encryption-transit', 'encryption', 'Chiffrement TLS en transit',
     'HSTS + redirection HTTPS forcée hors développement.', 'met', 'Program.cs'),
    ('encryption-secrets', 'encryption', 'Secrets applicatifs jamais stockés en clair',
     'Clés d''API : empreinte SHA-256 uniquement. Secrets MFA : chiffrés au repos (AES-GCM, Phase 47). ' ||
     'Secret webhook des connecteurs (Phase 12) reste en clair par nécessité (vérification HMAC entrante).',
     'partial', 'ApiKeyService.Hash, MfaSecretProtector'),
    ('audit-immutability', 'audit', 'Journal d''audit immuable et non falsifiable',
     'Trigger interdisant toute UPDATE/DELETE sur audit_log.', 'met', 'V030 forbid_audit_mutation'),
    ('audit-coverage', 'audit', 'Actions sensibles journalisées (qui/quoi/quand/avant/après)',
     'Trigger générique attaché à la quasi-totalité des tables métier.', 'met', 'audit_row()'),
    ('privacy-classification', 'privacy', 'Classification des données par niveau de sensibilité',
     'Seul un tag ponctuel existe sur les exports (normal/financial/restricted) — aucune taxonomie ' ||
     'générale appliquée aux entités/documents.', 'not_met', 'export_log.sensitivity (V026)'),
    ('privacy-consent', 'privacy', 'Gestion du consentement et droits des personnes',
     'Aucun flux d''accès/rectification/suppression/export au sens vie privée.', 'not_met', NULL),
    ('backup-encrypted', 'backup_dr', 'Sauvegardes chiffrées, vérifiées et testées',
     'Registre applicatif avec vérification et tests de restauration à double validation.',
     'met', 'V049, IContinuityService'),
    ('backup-dr-plan', 'backup_dr', 'RPO/RTO définis avec exercices de restauration',
     'État de reprise noté A-E, historique des tests.', 'met', 'DisasterRecoveryStatusAsync'),
    ('api-rate-limit', 'api_security', 'Limitation de débit sur les API sensibles',
     'Fenêtre fixe sur l''authentification, quota par minute par clé d''API.',
     'met', 'Program.cs rate limiter, api_key.rate_limit_per_minute'),
    ('api-ssrf', 'api_security', 'Protection SSRF sur les URLs sortantes',
     'Seul le schéma (http/https) est validé pour les cibles de webhooks sortants — aucun blocage ' ||
     'd''IP privée/loopback/lien-local. Écart documenté depuis la Phase 46.',
     'not_met', 'WebhookServices.cs'),
    ('payment-tokenization', 'payment', 'Aucune donnée de carte sensible stockée directement',
     'Abstraction PSP — le schéma ne porte aucun champ de carte bancaire.', 'met', 'PaymentGatewayService'),
    ('vendor-supply-chain', 'vendor', 'Dépendances verrouillées et suivies',
     'Versions figées (csproj/package-lock) mais aucun SBOM ni vérification de signature.',
     'partial', NULL)
ON CONFLICT (code) DO NOTHING;

-- ===================== 4. Accès d'urgence « break-glass » (§12) — RLS entreprise =============
-- Réutilise intégralement le moteur JIT de la Phase 22 (`user_role.valid_from/valid_until`,
-- `UserAssignmentService.GrantAsync` avec sa garde anti-élévation déjà en place) : cette table
-- porte uniquement la DEMANDE et sa décision, jamais un second mécanisme d'octroi de permission.
CREATE TABLE nexora.emergency_access_grant (
    id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id               uuid        NOT NULL REFERENCES nexora.company(id),
    requested_by             uuid        NOT NULL REFERENCES nexora.app_user(id),
    role_name                text        NOT NULL,
    scope_type               text        NOT NULL DEFAULT 'COMPANY',
    establishment_id         uuid,
    reason                   text        NOT NULL,
    requested_duration_hours smallint    NOT NULL,
    status                   text        NOT NULL DEFAULT 'pending',
    requested_at             timestamptz NOT NULL DEFAULT now(),
    decided_by               uuid        REFERENCES nexora.app_user(id),
    decided_at               timestamptz,
    decision_note            text,
    underlying_assignment_id uuid        REFERENCES nexora.user_role(id),
    revoked_at               timestamptz,
    revoked_by               uuid        REFERENCES nexora.app_user(id),
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_at               timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT emergency_access_grant_scope_chk CHECK (scope_type IN ('COMPANY','ESTABLISHMENT')),
    CONSTRAINT emergency_access_grant_duration_chk CHECK (requested_duration_hours BETWEEN 1 AND 8),
    CONSTRAINT emergency_access_grant_status_chk CHECK (status IN ('pending','approved','rejected','revoked')),
    CONSTRAINT emergency_access_grant_reason_chk CHECK (length(trim(reason)) >= 10),
    -- Séparation des responsabilités (§6) : jamais auto-approuvé, même par un super-administrateur.
    CONSTRAINT emergency_access_grant_self_decision_chk CHECK (decided_by IS NULL OR decided_by <> requested_by),
    CONSTRAINT emergency_access_grant_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);
CREATE INDEX emergency_access_grant_company_idx ON nexora.emergency_access_grant (company_id, status);

-- ===================== 5. Incidents de sécurité — extension de continuity_incident (§29-30) ==
-- Jamais un second registre d'incidents : `continuity_incident` (Phase 36) porte déjà sévérité/
-- runbook/post-mortem/audit — un simple discriminant de domaine suffit à couvrir les incidents de
-- sécurité (fuite d'identifiants, accès non autorisé…) sans dupliquer le concept ni son API.
ALTER TABLE nexora.continuity_incident ADD COLUMN domain text NOT NULL DEFAULT 'availability';
ALTER TABLE nexora.continuity_incident ADD CONSTRAINT continuity_incident_domain_chk
    CHECK (domain IN ('availability','security'));
ALTER TABLE nexora.continuity_incident DROP CONSTRAINT continuity_incident_category_chk;
ALTER TABLE nexora.continuity_incident ADD CONSTRAINT continuity_incident_category_chk CHECK (category IN (
    'server_failure','disk_failure','db_corruption','human_error','accidental_deletion','ransomware',
    'network_loss','cloud_provider','power','site_disaster','deploy_error','other',
    'breach','unauthorized_access','credential_leak','data_exfiltration','malware','phishing'));

-- ===================== 6. Registre d'appareils — enrichissement de user_session (§11) ========
-- Jamais une seconde table : une session EST déjà l'unité "appareil connecté" (device_label, IP,
-- user_agent, dernière activité, révocation). Il ne manquait que le type et le statut de confiance.
ALTER TABLE nexora.user_session ADD COLUMN device_type text NOT NULL DEFAULT 'unknown';
ALTER TABLE nexora.user_session ADD CONSTRAINT user_session_device_type_chk
    CHECK (device_type IN ('desktop','mobile','tablet','pos_terminal','api','unknown'));
ALTER TABLE nexora.user_session ADD COLUMN trusted boolean NOT NULL DEFAULT false;

-- ===================== 7. RLS + audit ========================================================
SELECT nexora.enable_company_rls('nexora.emergency_access_grant'::regclass);
-- `security_finding`/`security_certificate`/`compliance_control` : tables plateforme sans RLS
-- entreprise (comme `platform_plan`/`job_definition`) — accès filtré par `platform.security`.

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'security_finding','security_certificate','compliance_control','emergency_access_grant'
    ] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 8. Permissions (§12, §47) =============================================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('security.request_emergency_access', 'Sécurité — demander un accès d''urgence (break-glass)', 'security', 'request_emergency_access'),
    ('security.approve_emergency_access', 'Sécurité — approuver un accès d''urgence (break-glass)', 'security', 'approve_emergency_access')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v60(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v60('super_administrator', ARRAY['security.request_emergency_access','security.approve_emergency_access']);
SELECT pg_temp.grant_perms_v60('group_administrator', ARRAY['security.request_emergency_access','security.approve_emergency_access']);
SELECT pg_temp.grant_perms_v60('owner_direction',     ARRAY['security.request_emergency_access','security.approve_emergency_access']);
SELECT pg_temp.grant_perms_v60('manager',             ARRAY['security.request_emergency_access']);

-- ===================== 9. Job planifié (§53) ==================================================
INSERT INTO nexora.job_definition (key, name, category, description, owner) VALUES
    ('security.check_certificates', 'Vérification des échéances de certificats', 'technical',
     'Recalcule le statut de chaque certificat (actif/bientôt expiré/expiré) selon son échéance (§53).', 'scheduler')
ON CONFLICT (key) DO NOTHING;

SELECT nexora.attach_common_triggers();
