-- =============================================================================
-- NEXORA — V105 — RLS des tables d'identité (audit 2026-10 éd. 2, A-009 résidu)
--
-- app_user, user_role et access_scope étaient les seules tables d'entreprise sans RLS : leur isolation reposait sur le
-- code. Désormais, hors portée système (connexion, recherche d'identité, planificateur — qui posent bypass_rls) :
--   * access_scope : visible pour les entreprises et groupes de la session, et les périmètres PLATFORM ;
--   * user_role    : visible pour soi-même ou si le périmètre est visible ;
--   * app_user     : visible pour soi-même ou si l'utilisateur a (ou a eu) une affectation visible — un ancien
--                    employé reste nommé dans l'historique de son entreprise.
-- Les écritures suivent les mêmes règles. Idempotente.
-- =============================================================================

ALTER TABLE nexora.access_scope ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.access_scope FORCE ROW LEVEL SECURITY;
ALTER TABLE nexora.user_role ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.user_role FORCE ROW LEVEL SECURITY;
ALTER TABLE nexora.app_user ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.app_user FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS access_scope_isolation ON nexora.access_scope;
CREATE POLICY access_scope_isolation ON nexora.access_scope
    USING (nexora.rls_bypass()
           OR scope_type = 'PLATFORM'
           OR company_id = ANY (nexora.current_company_ids())
           OR group_id = ANY (nexora.current_group_ids())
           OR EXISTS (SELECT 1 FROM nexora.company c
                      WHERE c.group_id = access_scope.group_id AND c.id = ANY (nexora.current_company_ids()))
           OR EXISTS (SELECT 1 FROM nexora.establishment e
                      WHERE e.id = access_scope.establishment_id AND e.company_id = ANY (nexora.current_company_ids())))
    WITH CHECK (nexora.rls_bypass()
                OR company_id = ANY (nexora.current_company_ids())
                OR group_id = ANY (nexora.current_group_ids())
                OR EXISTS (SELECT 1 FROM nexora.establishment e
                           WHERE e.id = access_scope.establishment_id AND e.company_id = ANY (nexora.current_company_ids())));

DROP POLICY IF EXISTS user_role_isolation ON nexora.user_role;
CREATE POLICY user_role_isolation ON nexora.user_role
    USING (nexora.rls_bypass()
           OR user_id = nexora.current_user_id()
           OR EXISTS (SELECT 1 FROM nexora.access_scope s WHERE s.id = user_role.scope_id))
    WITH CHECK (nexora.rls_bypass()
                OR EXISTS (SELECT 1 FROM nexora.access_scope s
                           WHERE s.id = user_role.scope_id AND s.scope_type <> 'PLATFORM'));

DROP POLICY IF EXISTS app_user_isolation ON nexora.app_user;
CREATE POLICY app_user_isolation ON nexora.app_user
    USING (nexora.rls_bypass()
           OR id = nexora.current_user_id()
           OR EXISTS (SELECT 1 FROM nexora.user_role ur WHERE ur.user_id = app_user.id))
    WITH CHECK (nexora.rls_bypass()
                OR id = nexora.current_user_id()
                OR EXISTS (SELECT 1 FROM nexora.user_role ur WHERE ur.user_id = app_user.id));
