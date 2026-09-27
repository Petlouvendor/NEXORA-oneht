-- =============================================================================
-- NEXORA — V041 — Finance & trésorerie : cockpit de pilotage (Phase 28)
--
-- Le socle de trésorerie existe déjà : financial_account / financial_transaction /
-- cash_register / cash_session / cash_movement (V011), expense / budget / budget_line (V011),
-- bank_statement / bank_statement_line (V011), exchange_rate (V010), le moteur financier
-- centralisé (V022, PostingRuleSet), l'import & rapprochement bancaire (Phase 12), la
-- prévision de trésorerie (IFinancialReports.CashFlowForecastAsync, Phase 4).
--
-- Cette migration ajoute la **couche de contrôle & de pilotage** décrite aux §27-28, §32, §38 :
--   treasury_suspense_item : file d'attente des sommes non identifiées / non affectées (§32)
--   financial_close        : clôture financière d'une période avec contrôles horodatés (§38)
--   réglages treasury.*    : horizon de liquidité, âge maximal d'un suspens
--   permissions            : finance.close_period, finance.manage_suspense
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Comptes de suspense (§31-32) =================
CREATE TABLE nexora.treasury_suspense_item (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id),
    financial_account_id  uuid,
    kind                  text        NOT NULL DEFAULT 'unidentified',
    amount                nexora.money_amount NOT NULL,          -- signé : + entrée non identifiée, - sortie
    currency_id           uuid        NOT NULL REFERENCES nexora.currency(id),
    reference             text,
    reason                text        NOT NULL,
    source_type           text,                                  -- financial_transaction / bank_statement_line / …
    source_id             uuid,
    responsible_user_id   uuid REFERENCES nexora.app_user(id),
    resolution_due_date   date,
    status                text        NOT NULL DEFAULT 'open',
    resolution            text,
    resolved_at           timestamptz,
    resolved_by           uuid REFERENCES nexora.app_user(id),
    created_by            uuid REFERENCES nexora.app_user(id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT treasury_suspense_kind_chk CHECK (kind IN (
        'unidentified','payment_without_invoice','unallocated_fee',
        'bank_discrepancy','mobile_money_discrepancy','cash_variance','other')),
    CONSTRAINT treasury_suspense_status_chk CHECK (status IN ('open','resolved','written_off','cancelled')),
    CONSTRAINT treasury_suspense_amount_chk CHECK (amount <> 0),
    CONSTRAINT treasury_suspense_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT treasury_suspense_account_fk FOREIGN KEY (financial_account_id, company_id)
        REFERENCES nexora.financial_account (id, company_id)
);
CREATE INDEX treasury_suspense_company_status_idx
    ON nexora.treasury_suspense_item (company_id, status, resolution_due_date);
CREATE INDEX treasury_suspense_account_idx
    ON nexora.treasury_suspense_item (financial_account_id);

-- ===================== 2. Clôture financière (§38) =====================
CREATE TABLE nexora.financial_close (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid        NOT NULL REFERENCES nexora.company(id),
    accounting_period_id uuid        NOT NULL,
    as_of_date           date        NOT NULL,
    status               text        NOT NULL DEFAULT 'completed',
    controls             jsonb       NOT NULL DEFAULT '[]'::jsonb,  -- instantané horodaté des contrôles §38
    forced               boolean     NOT NULL DEFAULT false,
    note                 text,
    closed_at            timestamptz NOT NULL DEFAULT now(),
    closed_by            uuid REFERENCES nexora.app_user(id),
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT financial_close_status_chk CHECK (status IN ('completed','reopened')),
    CONSTRAINT financial_close_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT financial_close_period_fk FOREIGN KEY (accounting_period_id, company_id)
        REFERENCES nexora.accounting_period (id, company_id)
);
CREATE UNIQUE INDEX financial_close_period_asof_uk
    ON nexora.financial_close (accounting_period_id, as_of_date) WHERE status = 'completed';
CREATE INDEX financial_close_company_idx ON nexora.financial_close (company_id, as_of_date DESC);

-- ===================== 3. Réglages trésorerie (§22-23, §28, §32) =======
INSERT INTO nexora.setting_definition (key, category, value_type, default_value, allowed_values, max_scope, is_sensitive, affects_future, description) VALUES
    ('treasury.liquidity_horizon_days', 'treasury', 'number', '30', '[]', 'company', false, false, 'Horizon (jours) de l''analyse de liquidité et du gap de trésorerie'),
    ('treasury.suspense_max_age_days',  'treasury', 'number', '30', '[]', 'company', false, false, 'Âge (jours) au-delà duquel un suspens est signalé (anti-fraude §48)'),
    ('treasury.min_cash_alert',         'treasury', 'number', '0',  '[]', 'company', false, false, 'Seuil de trésorerie disponible sous lequel une alerte est levée')
ON CONFLICT (key) DO NOTHING;

-- ===================== 4. RLS (§34) & audit (§49) ======================
SELECT nexora.enable_company_rls('nexora.treasury_suspense_item'::regclass);
SELECT nexora.enable_company_rls('nexora.financial_close'::regclass);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['treasury_suspense_item','financial_close'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 5. Permissions (§46-47) =========================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('finance.close_period',    'Finance — clôturer une période financière',        'finance', 'close_period'),
    ('finance.manage_suspense', 'Finance — gérer les comptes / éléments de suspense', 'finance', 'manage_suspense')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v41(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v41('super_administrator', ARRAY['finance.close_period','finance.manage_suspense']);
SELECT pg_temp.grant_perms_v41('group_administrator', ARRAY['finance.close_period','finance.manage_suspense']);
SELECT pg_temp.grant_perms_v41('owner_direction',     ARRAY['finance.close_period','finance.manage_suspense']);
SELECT pg_temp.grant_perms_v41('chief_accountant',    ARRAY['finance.close_period','finance.manage_suspense']);
SELECT pg_temp.grant_perms_v41('senior_accountant',   ARRAY['finance.manage_suspense']);
SELECT pg_temp.grant_perms_v41('accountant',          ARRAY['finance.manage_suspense']);

SELECT nexora.attach_common_triggers();
