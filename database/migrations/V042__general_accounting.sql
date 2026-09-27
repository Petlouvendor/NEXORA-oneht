-- =============================================================================
-- NEXORA — V042 — Comptabilité générale : workflow manuel & clôture d'exercice (Phase 29)
--
-- Le moteur comptable existe déjà (Phase 4) : plan comptable `account` / `chart_of_accounts`,
-- `journal`, `accounting_period`, `journal_entry` / `journal_entry_line`, le moteur
-- événement → règle → écriture équilibrée (`PostingRuleSet` / `FinancialEngine`), l'extourne
-- (`ReversalService`), la clôture / réouverture de période (`AccountingPeriodService`), les
-- états (`IFinancialReports` : balance, grand livre, résultat, bilan). La consolidation &
-- l'intercompany sont en Phase 9 ; l'analytique (`cost_center_id` sur les lignes) en Phase 7.
--
-- Cette migration ajoute le **workflow comptable manuel** (§9-19, §47) :
--   journal_entry.origin      : origine de l'écriture (system / manual / opening / closing / …)
--   accounting_year_close     : clôture d'exercice (résultat, report, à-nouveaux) horodatée
--   clé de mapping current_year_earnings : résultat de l'exercice (capitaux propres)
--   permissions accounting.create, accounting.carry_forward
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Origine de l'écriture (§9, §12, §19) =========
ALTER TABLE nexora.journal_entry
    ADD COLUMN IF NOT EXISTS origin text NOT NULL DEFAULT 'system';
ALTER TABLE nexora.journal_entry DROP CONSTRAINT IF EXISTS journal_entry_origin_chk;
ALTER TABLE nexora.journal_entry
    ADD CONSTRAINT journal_entry_origin_chk CHECK (origin IN (
        'system','manual','opening','closing','reversal','adjustment'));
-- Les écritures déjà présentes gardent 'system' ; l'extourne existante reste identifiable
-- par reversal_of_entry_id — on aligne rétroactivement son origine.
UPDATE nexora.journal_entry SET origin = 'reversal'
 WHERE reversal_of_entry_id IS NOT NULL AND origin = 'system';
CREATE INDEX IF NOT EXISTS journal_entry_origin_idx ON nexora.journal_entry (company_id, origin);

-- ===================== 2. Clôture d'exercice (§16, §18-19) =============
CREATE TABLE nexora.accounting_year_close (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id),
    accounting_period_id  uuid        NOT NULL,
    net_result            nexora.money_amount NOT NULL DEFAULT 0,
    result_entry_id       uuid,                      -- écriture de solde des comptes de résultat
    carry_forward_entry_id uuid,                     -- écriture d'à-nouveaux de l'exercice suivant
    next_period_id        uuid,
    controls              jsonb       NOT NULL DEFAULT '[]'::jsonb,
    forced                boolean     NOT NULL DEFAULT false,
    note                  text,
    status                text        NOT NULL DEFAULT 'completed',
    closed_at             timestamptz NOT NULL DEFAULT now(),
    closed_by             uuid REFERENCES nexora.app_user(id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT accounting_year_close_status_chk CHECK (status IN ('completed','reopened')),
    CONSTRAINT accounting_year_close_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT accounting_year_close_period_uk UNIQUE (accounting_period_id),
    CONSTRAINT accounting_year_close_period_fk FOREIGN KEY (accounting_period_id, company_id)
        REFERENCES nexora.accounting_period (id, company_id)
);
CREATE INDEX accounting_year_close_company_idx ON nexora.accounting_year_close (company_id, closed_at DESC);

-- ===================== 3. Mapping : résultat de l'exercice ============
ALTER TABLE nexora.account_mapping DROP CONSTRAINT account_mapping_key_chk;
ALTER TABLE nexora.account_mapping ADD CONSTRAINT account_mapping_key_chk CHECK (mapping_key IN (
    'sales_revenue','sales_tax_output','accounts_receivable','accounts_payable',
    'purchases','inventory','cogs','purchase_tax_input','expense_default',
    'cash_short_over','fx_gain','fx_loss','rounding','bank_charges','suspense',
    'cash','bank','mobile_money','retained_earnings','current_year_earnings',
    'inventory_adjustment','inventory_loss','goods_in_transit','purchase_price_variance',
    'sales_returns','store_credit','tips_payable','bad_debt'));

-- ===================== 4. RLS (§55) & audit (§48) =====================
SELECT nexora.enable_company_rls('nexora.accounting_year_close'::regclass);
DROP TRIGGER IF EXISTS trg_audit_accounting_year_close ON nexora.accounting_year_close;
CREATE TRIGGER trg_audit_accounting_year_close
    AFTER INSERT OR UPDATE OR DELETE ON nexora.accounting_year_close
    FOR EACH ROW EXECUTE FUNCTION nexora.audit_row();

-- ===================== 5. Permissions (§49-50) ========================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('accounting.carry_forward', 'Comptabilité — à-nouveaux & clôture d''exercice', 'accounting', 'carry_forward')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v42(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v42('super_administrator', ARRAY['accounting.carry_forward']);
SELECT pg_temp.grant_perms_v42('group_administrator', ARRAY['accounting.carry_forward']);
SELECT pg_temp.grant_perms_v42('owner_direction',     ARRAY['accounting.carry_forward']);
SELECT pg_temp.grant_perms_v42('chief_accountant',    ARRAY['accounting.carry_forward']);

SELECT nexora.attach_common_triggers();
