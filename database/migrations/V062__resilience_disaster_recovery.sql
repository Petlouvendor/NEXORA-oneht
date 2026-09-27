-- =============================================================================
-- NEXORA — V062 — Résilience, Haute Disponibilité & Disaster Recovery (Phase 48)
--
-- Le registre applicatif de continuité existe déjà et reste VALIDÉ, pas reconstruit (Phase 36,
-- V049 ; étendu Phase 47 V060) : `backup_policy`/`backup`/`restore_run` (catalogue, vérification,
-- tests de restauration, restauration production à 2 personnes), `continuity_incident` (sévérité,
-- runbook générique 9 étapes, post-mortem obligatoire, domaine availability/security), état de
-- reprise noté A-E (`DisasterRecoveryStatusAsync`). Le cahier des charges de cette phase redemande
-- presque mot pour mot ce socle (RPO/RTO, 3-2-1, immutabilité, chiffrement, PITR, HA/failover —
-- tous déjà couverts ou explicitement hors périmètre applicatif en Phase 36).
--
-- Ce qui manquait réellement : des RUNBOOKS NOMMÉS par scénario (§37 — le registre n'avait que
-- 9 étapes génériques, jamais de contenu concret par catégorie d'incident), un registre d'EXERCICES
-- DR au-delà du simple test de restauration (§44-45 — tabletop, bascule simulée, panne réseau…),
-- une action de CONFINEMENT réelle pour un incident de sécurité/ransomware (§32, §55 — révoquer
-- sessions et clés API de l'entreprise touchée, pas seulement documenter l'intention), et une PAGE
-- DE STATUT composant l'état de santé déjà mesuré par le Phase 41 (diagnostics) plutôt qu'un
-- second calcul (§39).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Registre d'exercices DR (§44-45) — RLS entreprise ==================
-- Distinct de `restore_run` (Phase 36) : un restore_run exige toujours un backup_id concret,
-- un exercice DR peut être un tabletop ou une simulation de panne réseau, sans sauvegarde en jeu.
CREATE TABLE nexora.dr_test (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL REFERENCES nexora.company(id),
    test_type          text        NOT NULL,
    title              text        NOT NULL,
    scheduled_for      date,
    executed_at        timestamptz,
    status             text        NOT NULL DEFAULT 'scheduled',
    scope              text,
    participants       jsonb       NOT NULL DEFAULT '[]'::jsonb,
    findings           text,
    corrective_actions text,
    next_test_due      date,
    created_by         uuid        REFERENCES nexora.app_user(id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT dr_test_type_chk CHECK (test_type IN (
        'tabletop', 'restore', 'failover_db', 'failover_app', 'network_outage',
        'server_outage', 'storage_outage', 'ransomware_simulation', 'site_unavailable', 'return_to_normal')),
    CONSTRAINT dr_test_status_chk CHECK (status IN ('scheduled', 'passed', 'failed', 'cancelled'))
);
CREATE INDEX dr_test_company_idx ON nexora.dr_test (company_id, status, scheduled_for);

-- ===================== 2. Confinement d'incident (§32, §55) ===================================
-- Trace des actions de confinement RÉELLEMENT exécutées (ex. verrouillage d'urgence) — jamais
-- seulement une intention documentée dans le post-mortem après coup.
ALTER TABLE nexora.continuity_incident ADD COLUMN containment_actions jsonb NOT NULL DEFAULT '[]'::jsonb;

-- ===================== 3. Types de sauvegarde étendus (§11) ===================================
ALTER TABLE nexora.backup DROP CONSTRAINT backup_kind_chk;
ALTER TABLE nexora.backup ADD CONSTRAINT backup_kind_chk
    CHECK (kind IN ('full', 'incremental', 'differential', 'config', 'documents', 'snapshot', 'metadata', 'keys'));

-- ===================== 4. RLS + audit ==========================================================
SELECT nexora.enable_company_rls('nexora.dr_test'::regclass);

DROP TRIGGER IF EXISTS trg_audit_dr_test ON nexora.dr_test;
CREATE TRIGGER trg_audit_dr_test AFTER INSERT OR UPDATE OR DELETE ON nexora.dr_test
    FOR EACH ROW EXECUTE FUNCTION nexora.audit_row();

-- Aucune nouvelle permission : réutilise `continuity.{view,manage,restore}` (Phase 36) — un
-- exercice DR se gère comme un test de restauration, un confinement comme une restauration
-- production (même niveau de confiance élevé, `continuity.restore`).

SELECT nexora.attach_common_triggers();
