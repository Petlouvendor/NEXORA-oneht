-- =============================================================================
-- NEXORA — V101 — Politiques d'écriture et moindre privilège (audit 2026-10, A-029, A-030)
--
-- A-029. La politique de `role` laissait aussi ÉCRIRE les lignes globales (company_id NULL : rôles système partagés
-- par toutes les entreprises) ; `role_permission` n'avait pas de RLS ; `audit_log` acceptait toute insertion
-- (WITH CHECK true). L'application refuse déjà de modifier un rôle système : la base le refuse désormais aussi.
--   • role            : lecture des rôles système + des rôles de ses entreprises ; écriture de ces derniers seulement.
--   • role_permission : mêmes règles, à travers le rôle concerné.
--   • audit_log       : une insertion porte sur une entreprise du contexte (ou aucune entreprise).
-- La portée système (nexora.bypass_rls) garde tous les droits : migrations, amorçage, tâches planifiées.
--
-- A-030. Le rôle applicatif n'a pas à écrire dans le registre des migrations ni à modifier ou supprimer le journal
-- d'audit (un déclencheur le bloquait déjà) : privilèges retirés.
-- Idempotente.
-- =============================================================================

-- --- role ---------------------------------------------------------------------------------------------------
DROP POLICY IF EXISTS role_isolation ON nexora.role;
DROP POLICY IF EXISTS role_read ON nexora.role;
DROP POLICY IF EXISTS role_write ON nexora.role;
CREATE POLICY role_read ON nexora.role FOR SELECT
    USING (nexora.rls_bypass() OR company_id IS NULL OR company_id = ANY (nexora.current_company_ids()));
CREATE POLICY role_write ON nexora.role FOR ALL
    USING (nexora.rls_bypass() OR company_id = ANY (nexora.current_company_ids()))
    WITH CHECK (nexora.rls_bypass() OR company_id = ANY (nexora.current_company_ids()));

-- --- role_permission ------------------------------------------------------------------------------------------
ALTER TABLE nexora.role_permission ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.role_permission FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS role_permission_read ON nexora.role_permission;
DROP POLICY IF EXISTS role_permission_write ON nexora.role_permission;
CREATE POLICY role_permission_read ON nexora.role_permission FOR SELECT
    USING (nexora.rls_bypass() OR EXISTS (
        SELECT 1 FROM nexora.role r
        WHERE r.id = role_permission.role_id
          AND (r.company_id IS NULL OR r.company_id = ANY (nexora.current_company_ids()))));
CREATE POLICY role_permission_write ON nexora.role_permission FOR ALL
    USING (nexora.rls_bypass() OR EXISTS (
        SELECT 1 FROM nexora.role r
        WHERE r.id = role_permission.role_id AND r.company_id = ANY (nexora.current_company_ids())))
    WITH CHECK (nexora.rls_bypass() OR EXISTS (
        SELECT 1 FROM nexora.role r
        WHERE r.id = role_permission.role_id AND r.company_id = ANY (nexora.current_company_ids())));

-- --- audit_log ------------------------------------------------------------------------------------------------
DROP POLICY IF EXISTS audit_isolation ON nexora.audit_log;
CREATE POLICY audit_isolation ON nexora.audit_log FOR ALL
    USING (nexora.rls_bypass() OR company_id = ANY (nexora.current_company_ids()))
    WITH CHECK (nexora.rls_bypass() OR company_id IS NULL OR company_id = ANY (nexora.current_company_ids()));

-- --- A-030 : moindre privilège du rôle applicatif ------------------------------------------------------------
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_app') THEN
        REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON nexora.schema_migration FROM nexora_app;
        REVOKE UPDATE, DELETE, TRUNCATE ON nexora.audit_log FROM nexora_app;
    END IF;
END $$;
