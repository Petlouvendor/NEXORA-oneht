-- =============================================================================
-- NEXORA — V030 — Sécurité, Audit, Conformité & Anti-fraude (Phase 11)
-- La sécurité est une fonction centrale (§1). Cette migration ajoute :
--  • le moteur de détection anti-fraude (règles + signaux notés)
--  • la ré-authentification renforcée (step-up)
--  • le registre des sessions + « déconnexion globale »
--  • la politique de sécurité par entreprise
--  • le durcissement du journal d'audit (immuable, non modifiable sans trace)
-- Réutilise audit_log / approval_policy / approval_request / approval_action (V016).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Moteur anti-fraude (§14-18) =======================

-- Catalogue de règles — donnée de référence (pas de company_id, hors RLS).
CREATE TABLE nexora.fraud_rule (
    code           text PRIMARY KEY,
    name           text        NOT NULL,
    category       text        NOT NULL,               -- pos | stock | finance
    subject_type   text        NOT NULL,               -- user | terminal | product | supplier | cash_session
    window_minutes integer     NOT NULL DEFAULT 1440,
    threshold      numeric(20,6) NOT NULL DEFAULT 0,
    severity_base  text        NOT NULL DEFAULT 'medium',
    description    text        NOT NULL DEFAULT '',
    CONSTRAINT fraud_rule_category_chk CHECK (category IN ('pos','stock','finance')),
    CONSTRAINT fraud_rule_severity_chk CHECK (severity_base IN ('low','medium','high','critical'))
);

INSERT INTO nexora.fraud_rule (code, name, category, subject_type, window_minutes, threshold, severity_base, description) VALUES
 ('pos_frequent_voids',       'Annulations de vente fréquentes',          'pos',     'user',         1440, 3,  'high',     'Plus de N ventes annulées par le même utilisateur sur la fenêtre.'),
 ('pos_post_payment_void',    'Ventes annulées après paiement',           'pos',     'user',         1440, 1,  'critical', 'Vente passée à « void » alors qu''un paiement était enregistré.'),
 ('pos_abnormal_refunds',     'Remboursements anormaux',                  'pos',     'user',         1440, 3,  'high',     'Plus de N retours/remboursements par le même utilisateur.'),
 ('pos_unusual_discounts',    'Remises inhabituelles',                    'pos',     'user',         1440, 2,  'medium',   'Plusieurs ventes avec remise > seuil relatif.'),
 ('pos_repeated_price_change','Modifications de prix répétées',           'pos',     'user',         1440, 3,  'medium',   'Prix de vente forcé (original_unit_price ≠ unit_price) plusieurs fois.'),
 ('pos_cash_variance',        'Écarts de caisse répétés',                 'pos',     'cash_session', 10080, 2, 'high',     'Plusieurs sessions clôturées avec écart non nul.'),
 ('pos_unusual_credit_sales', 'Ventes à crédit inhabituelles',            'pos',     'user',         1440, 3,  'medium',   'Plusieurs ventes à crédit par un utilisateur qui n''en fait pas d''habitude.'),
 ('stock_frequent_adjustments','Ajustements de stock fréquents',          'stock',   'user',         10080, 3, 'high',     'Plus de N ajustements/pertes validés par le même utilisateur.'),
 ('stock_repeated_losses',    'Pertes répétées',                         'stock',   'product',      43200, 3,  'medium',   'Plusieurs mouvements de perte/casse sur un même produit.'),
 ('finance_reversals',        'Extournes répétées',                      'finance', 'user',         43200, 2,  'high',     'Plusieurs écritures extournées par le même utilisateur.'),
 ('finance_expense_under_threshold','Dépenses juste sous un seuil',       'finance', 'user',         43200, 3,  'high',     'Plusieurs dépenses proches (≤ 5 %) d''un seuil d''approbation.'),
 ('finance_supplier_change',  'Modifications de coordonnées fournisseur', 'finance', 'supplier',     43200, 1,  'critical', 'Bénéficiaire / coordonnées d''un fournisseur modifiés.')
ON CONFLICT (code) DO NOTHING;

-- Réglage par entreprise (activation + seuils).
CREATE TABLE nexora.fraud_rule_setting (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL REFERENCES nexora.company(id),
    rule_code      text        NOT NULL REFERENCES nexora.fraud_rule(code),
    enabled        boolean     NOT NULL DEFAULT true,
    threshold      numeric(20,6),
    window_minutes integer,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fraud_rule_setting_uk UNIQUE (company_id, rule_code)
);
CREATE INDEX fraud_rule_setting_company_idx ON nexora.fraud_rule_setting (company_id);

-- Signaux notés, en attente de revue humaine (§17-18).
CREATE TABLE nexora.fraud_signal (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid        NOT NULL REFERENCES nexora.company(id),
    rule_code       text        NOT NULL,
    category        text        NOT NULL,
    severity        text        NOT NULL,
    risk_score      smallint    NOT NULL DEFAULT 50,
    subject_type    text        NOT NULL,
    subject_id      uuid,
    subject_label   text,
    actor_user_id   uuid,
    window_from     timestamptz NOT NULL,
    window_to       timestamptz NOT NULL,
    observed_value  numeric(20,6) NOT NULL DEFAULT 0,
    threshold       numeric(20,6) NOT NULL DEFAULT 0,
    detail          text        NOT NULL DEFAULT '',
    status          text        NOT NULL DEFAULT 'open',
    reviewed_by     uuid        REFERENCES nexora.app_user(id),
    reviewed_at     timestamptz,
    review_note     text,
    detected_at     timestamptz NOT NULL DEFAULT now(),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fraud_signal_severity_chk CHECK (severity IN ('low','medium','high','critical')),
    CONSTRAINT fraud_signal_score_chk    CHECK (risk_score BETWEEN 0 AND 100),
    CONSTRAINT fraud_signal_status_chk   CHECK (status IN ('open','reviewing','confirmed','false_positive','dismissed')),
    CONSTRAINT fraud_signal_dedup_uk UNIQUE NULLS NOT DISTINCT (company_id, rule_code, subject_type, subject_id, window_from)
);
CREATE INDEX fraud_signal_company_status_idx ON nexora.fraud_signal (company_id, status, detected_at DESC);

-- ===================== 2. Ré-authentification renforcée (§7, §36) ===========
CREATE TABLE nexora.step_up_challenge (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     uuid        NOT NULL REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    purpose     text        NOT NULL,
    token_hash  text        NOT NULL,
    issued_at   timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL,
    consumed_at timestamptz,
    CONSTRAINT step_up_challenge_purpose_chk CHECK (purpose IN (
        'period_close','period_reopen','permission_change','financial_exceptional',
        'consolidation_publish','device_revoke','sensitive_export','other'))
);
CREATE INDEX step_up_challenge_user_idx ON nexora.step_up_challenge (user_id, purpose, expires_at DESC);

-- ===================== 3. Registre des sessions (§6, §30) ===================
ALTER TABLE nexora.app_user ADD COLUMN security_epoch timestamptz NOT NULL DEFAULT now();

CREATE TABLE nexora.user_session (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id           uuid        NOT NULL REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    active_company_id uuid,
    device_label      text,
    ip_address        inet,
    user_agent        text,
    issued_at         timestamptz NOT NULL DEFAULT now(),
    last_seen_at      timestamptz NOT NULL DEFAULT now(),
    expires_at        timestamptz,
    revoked_at        timestamptz,
    revoked_by        uuid        REFERENCES nexora.app_user(id),
    revoke_reason     text,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX user_session_user_idx ON nexora.user_session (user_id, issued_at DESC);

-- ===================== 4. Politique de sécurité (§7, §21, §31) ==============
CREATE TABLE nexora.security_policy (
    id                              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id                      uuid        NOT NULL UNIQUE REFERENCES nexora.company(id),
    mfa_required_roles              jsonb       NOT NULL DEFAULT '["super_administrator","group_administrator","owner_direction","chief_accountant"]'::jsonb,
    session_ttl_minutes             integer     NOT NULL DEFAULT 60,
    step_up_ttl_minutes             integer     NOT NULL DEFAULT 10,
    sensitive_export_requires_approval boolean  NOT NULL DEFAULT false,
    export_watermark                boolean     NOT NULL DEFAULT true,
    audit_retention_days            integer     NOT NULL DEFAULT 2555,   -- ~7 ans par défaut
    config                          jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at                      timestamptz NOT NULL DEFAULT now(),
    updated_at                      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT security_policy_ttl_chk CHECK (session_ttl_minutes > 0 AND step_up_ttl_minutes > 0),
    CONSTRAINT security_policy_retention_chk CHECK (audit_retention_days >= 0)
);

-- ===================== 5. Durcissement du journal d'audit (§12, §39) ========
CREATE OR REPLACE FUNCTION nexora.forbid_audit_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Le journal d''audit est immuable (Phase 11 §12) : % interdit', TG_OP
        USING ERRCODE = 'insufficient_privilege';
END;
$$;
DROP TRIGGER IF EXISTS trg_audit_log_immutable ON nexora.audit_log;
CREATE TRIGGER trg_audit_log_immutable
    BEFORE UPDATE OR DELETE ON nexora.audit_log
    FOR EACH ROW EXECUTE FUNCTION nexora.forbid_audit_mutation();

-- Étend l'audit aux tables sensibles de la Phase 11.
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['fraud_signal','approval_request','approval_action','security_policy',
                             'user_role','access_scope','supplier','customer','sync_device'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 6. Permissions (§8, §41) ============================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('security.audit_view',   'Sécurité — consulter le journal d''audit',   'security', 'audit_view'),
    ('security.fraud_review', 'Sécurité — revoir les signaux anti-fraude',  'security', 'fraud_review'),
    ('security.approvals',    'Sécurité — approuver / refuser les demandes', 'security', 'approvals'),
    ('security.sessions',     'Sécurité — gérer les sessions et appareils',  'security', 'sessions'),
    ('security.configure',    'Sécurité — politique de sécurité',            'security', 'configure')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v30(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v30('super_administrator', ARRAY['security.%']);
SELECT pg_temp.grant_perms_v30('group_administrator', ARRAY['security.%']);
SELECT pg_temp.grant_perms_v30('owner_direction',     ARRAY['security.%']);
SELECT pg_temp.grant_perms_v30('chief_accountant',    ARRAY['security.audit_view','security.fraud_review','security.approvals']);
SELECT pg_temp.grant_perms_v30('manager',             ARRAY['security.audit_view','security.fraud_review','security.approvals','security.sessions']);
SELECT pg_temp.grant_perms_v30('senior_accountant',   ARRAY['security.audit_view','security.approvals']);
SELECT pg_temp.grant_perms_v30('accountant',          ARRAY['security.audit_view']);
SELECT pg_temp.grant_perms_v30('auditor',             ARRAY['security.audit_view','security.fraud_review']);
SELECT pg_temp.grant_perms_v30('stock_manager',       ARRAY['security.fraud_review']);

-- ===================== 7. RLS ============================================
-- Tables propres à l'utilisateur.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['user_session','step_up_challenge'] LOOP
        EXECUTE format('ALTER TABLE nexora.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE nexora.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS %I_isolation ON nexora.%I', t, t);
        EXECUTE format(
            'CREATE POLICY %1$I_isolation ON nexora.%1$I
               USING (nexora.rls_bypass() OR user_id = nexora.current_user_id())
               WITH CHECK (nexora.rls_bypass() OR user_id = nexora.current_user_id())', t);
    END LOOP;
END;
$$;

-- Rattrapage entreprise pour les nouvelles tables métier post-V020.
DO $$
DECLARE
    r record;
    excluded text[] := ARRAY['audit_log','notification','schema_migration','sector_module_preset','fraud_rule'];
BEGIN
    FOR r IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        LEFT JOIN pg_tables pt ON pt.schemaname = 'nexora' AND pt.tablename = c.table_name
        WHERE c.table_schema = 'nexora' AND c.column_name = 'company_id' AND c.is_nullable = 'NO'
          AND t.table_type = 'BASE TABLE' AND c.table_name <> ALL (excluded) AND (pt.rowsecurity IS NOT TRUE)
    LOOP
        PERFORM nexora.enable_company_rls(format('nexora.%I', r.table_name)::regclass);
    END LOOP;
END;
$$;

-- ===================== 8. Politique de sécurité par défaut =================
INSERT INTO nexora.security_policy (company_id)
SELECT id FROM nexora.company
ON CONFLICT (company_id) DO NOTHING;

INSERT INTO nexora.fraud_rule_setting (company_id, rule_code)
SELECT c.id, fr.code FROM nexora.company c CROSS JOIN nexora.fraud_rule fr
ON CONFLICT (company_id, rule_code) DO NOTHING;

SELECT nexora.attach_common_triggers();
