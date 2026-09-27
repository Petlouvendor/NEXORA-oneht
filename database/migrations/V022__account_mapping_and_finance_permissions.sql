-- =============================================================================
-- NEXORA — V022 — Mapping comptable des événements + permissions financières
-- Phase 4 §5 (« mapping des comptes vers les événements métier »), §31.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Mapping « rôle comptable » → compte, par entreprise (§5) --------------
CREATE TABLE nexora.account_mapping (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id  uuid        NOT NULL REFERENCES nexora.company(id),
    mapping_key text        NOT NULL,
    account_id  uuid        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT account_mapping_key_chk CHECK (mapping_key IN (
        'sales_revenue','sales_tax_output','accounts_receivable','accounts_payable',
        'purchases','inventory','cogs','purchase_tax_input','expense_default',
        'cash_short_over','fx_gain','fx_loss','rounding','bank_charges','suspense',
        'cash','bank','mobile_money','retained_earnings')),
    CONSTRAINT account_mapping_company_key_uk UNIQUE (company_id, mapping_key),
    CONSTRAINT account_mapping_account_fk FOREIGN KEY (account_id, company_id)
        REFERENCES nexora.account (id, company_id)
);
CREATE INDEX account_mapping_company_idx ON nexora.account_mapping (company_id);

-- --- Idempotence des écritures générées par le moteur (§33) ---------------
ALTER TABLE nexora.journal_entry
    ADD COLUMN source_operation_uuid uuid;
CREATE UNIQUE INDEX journal_entry_source_op_uk
    ON nexora.journal_entry (source_operation_uuid) WHERE source_operation_uuid IS NOT NULL;

-- --- Différence de change portée par une ligne de paiement (§22) ---------
ALTER TABLE nexora.payment_allocation
    ADD COLUMN fx_difference nexora.money_amount NOT NULL DEFAULT 0;

-- --- Permissions financières (§31) --------------------------------------
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('finance.pay',                'Finance — effectuer un paiement',        'finance',    'pay'),
    ('finance.allocate',           'Finance — affecter un paiement',         'finance',    'allocate'),
    ('finance.manage_cash',        'Finance — gérer la caisse',              'finance',    'manage_cash'),
    ('accounting.validate',        'Comptabilité — valider une écriture',    'accounting', 'validate'),
    ('accounting.reverse',         'Comptabilité — extourner une écriture',  'accounting', 'reverse'),
    ('accounting.manage_chart',    'Comptabilité — gérer le plan comptable', 'accounting', 'manage_chart'),
    ('accounting.reconcile',       'Comptabilité — rapprochement',           'accounting', 'reconcile')
ON CONFLICT (code) DO NOTHING;

-- Rattachement aux rôles système existants.
CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v22(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id
    FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v22('super_administrator', ARRAY['finance.%','accounting.%']);
SELECT pg_temp.grant_perms_v22('group_administrator', ARRAY['finance.%','accounting.%']);
SELECT pg_temp.grant_perms_v22('owner_direction',     ARRAY['finance.%','accounting.%']);
SELECT pg_temp.grant_perms_v22('chief_accountant',    ARRAY['finance.%','accounting.%']);
SELECT pg_temp.grant_perms_v22('senior_accountant',   ARRAY[
    'finance.pay','finance.allocate','finance.manage_cash','finance.reconcile','finance.transfer',
    'accounting.validate','accounting.reverse','accounting.reconcile','accounting.manage_chart']);
SELECT pg_temp.grant_perms_v22('accountant',          ARRAY[
    'finance.pay','finance.allocate','finance.reconcile','accounting.validate','accounting.reconcile']);
SELECT pg_temp.grant_perms_v22('cashier',             ARRAY['finance.pay','finance.manage_cash']);

SELECT nexora.attach_common_triggers();
