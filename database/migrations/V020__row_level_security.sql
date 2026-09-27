-- =============================================================================
-- NEXORA — V020 — Row-Level Security multi-tenant (§34)
-- Isolation par entreprise / groupe appliquée au niveau du moteur.
-- L'API positionne par transaction :
--   SET LOCAL nexora.company_ids = '<uuid>,<uuid>';
--   SET LOCAL nexora.group_ids   = '<uuid>';
--   SET LOCAL nexora.user_id     = '<uuid>';
--   SET LOCAL nexora.bypass_rls  = 'on';   -- tâches système uniquement
-- =============================================================================
SET search_path TO nexora, public;

-- --- Rôle applicatif (sans BYPASSRLS) --------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_app') THEN
        CREATE ROLE nexora_app LOGIN PASSWORD 'change_me_in_deployment' NOBYPASSRLS;
    END IF;
END;
$$;

GRANT USAGE ON SCHEMA nexora TO nexora_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA nexora TO nexora_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA nexora TO nexora_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA nexora TO nexora_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA nexora GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nexora_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA nexora GRANT USAGE, SELECT ON SEQUENCES TO nexora_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA nexora GRANT EXECUTE ON FUNCTIONS TO nexora_app;

-- --- Isolation entreprise : toutes les tables métier avec company_id NOT NULL --
DO $$
DECLARE
    r record;
    excluded text[] := ARRAY['audit_log','notification'];
BEGIN
    FOR r IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.table_schema = 'nexora'
          AND c.column_name  = 'company_id'
          AND c.is_nullable  = 'NO'
          AND t.table_type   = 'BASE TABLE'
          AND c.table_name <> ALL (excluded)
    LOOP
        PERFORM nexora.enable_company_rls(format('nexora.%I', r.table_name)::regclass);
    END LOOP;
END;
$$;

-- --- Entreprise : visible par périmètre entreprise OU groupe --------
ALTER TABLE nexora.company ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.company FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS company_isolation ON nexora.company;
CREATE POLICY company_isolation ON nexora.company
    USING (nexora.rls_bypass()
        OR id = ANY (nexora.current_company_ids())
        OR group_id = ANY (nexora.current_group_ids()))
    WITH CHECK (nexora.rls_bypass()
        OR id = ANY (nexora.current_company_ids())
        OR group_id = ANY (nexora.current_group_ids()));

-- --- Groupe et tables groupe --------------------------------------
ALTER TABLE nexora.business_group ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.business_group FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS group_isolation ON nexora.business_group;
CREATE POLICY group_isolation ON nexora.business_group
    USING (nexora.rls_bypass() OR id = ANY (nexora.current_group_ids()))
    WITH CHECK (nexora.rls_bypass() OR id = ANY (nexora.current_group_ids()));

SELECT nexora.enable_group_rls('nexora.intercompany_transaction'::regclass);
SELECT nexora.enable_group_rls('nexora.consolidation_profile'::regclass);
SELECT nexora.enable_group_rls('nexora.consolidation_scope'::regclass);
SELECT nexora.enable_group_rls('nexora.consolidation_adjustment'::regclass);

-- --- Audit : lecture strictement limitée au périmètre entreprise --
ALTER TABLE nexora.audit_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.audit_log FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS audit_isolation ON nexora.audit_log;
CREATE POLICY audit_isolation ON nexora.audit_log
    USING (nexora.rls_bypass() OR company_id = ANY (nexora.current_company_ids()))
    WITH CHECK (true);   -- écrit par trigger SECURITY INVOKER, jamais bloqué

-- --- Notifications : propres à l'utilisateur courant --------------
ALTER TABLE nexora.notification ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.notification FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS notification_isolation ON nexora.notification;
CREATE POLICY notification_isolation ON nexora.notification
    USING (nexora.rls_bypass() OR user_id = nexora.current_user_id())
    WITH CHECK (nexora.rls_bypass() OR user_id = nexora.current_user_id());

-- --- Rôles : globaux (company_id NULL) ou propres à l'entreprise --
ALTER TABLE nexora.role ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.role FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS role_isolation ON nexora.role;
CREATE POLICY role_isolation ON nexora.role
    USING (nexora.rls_bypass() OR company_id IS NULL OR company_id = ANY (nexora.current_company_ids()))
    WITH CHECK (nexora.rls_bypass() OR company_id IS NULL OR company_id = ANY (nexora.current_company_ids()));
