-- =============================================================================
-- NEXORA — V040 — Recouvrement & crédit client (Phase 27 §21-27, §37)
-- Le socle crédit client existe déjà : customer / customer_account (V006),
-- customer_receivable (V012), ICreditControl (Phase 6), dunning_policy + exécuteur
-- de relances (V033/Phase 14), aging (Phase 7). Cette migration ajoute le **workflow
-- de recouvrement** :
--   customer_promise      : promesses de paiement (§24)
--   customer_dispute      : litiges client (§25)
--   customer_block        : blocages crédit / vente / livraison / compte (§26-27)
--   receivable_write_off  : radiation contrôlée d'une créance (§37)
--   customer_receivable.status : IN_DISPUTE, WRITTEN_OFF
--   account_mapping        : clé « bad_debt » (charge sur créances irrécouvrables)
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Promesse de paiement (§24) ===================
CREATE TABLE nexora.customer_promise (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL REFERENCES nexora.company(id),
    customer_id    uuid        NOT NULL,
    amount         nexora.money_amount NOT NULL,
    kept_amount    nexora.money_amount NOT NULL DEFAULT 0,
    snapshot_remaining nexora.money_amount NOT NULL DEFAULT 0,  -- solde des créances visées à la création
    promised_date  date        NOT NULL,
    channel        text        NOT NULL DEFAULT 'phone',
    receivable_ids uuid[]      NOT NULL DEFAULT '{}',
    status         text        NOT NULL DEFAULT 'open',
    note           text,
    created_by     uuid REFERENCES nexora.app_user(id),
    settled_at     timestamptz,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_promise_amount_chk CHECK (amount > 0 AND kept_amount >= 0),
    CONSTRAINT customer_promise_channel_chk CHECK (channel IN ('phone','email','sms','whatsapp','visit','internal')),
    CONSTRAINT customer_promise_status_chk CHECK (status IN ('open','kept','partially_kept','broken','cancelled')),
    CONSTRAINT customer_promise_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT customer_promise_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id)
);
CREATE INDEX customer_promise_company_status_idx ON nexora.customer_promise (company_id, status, promised_date);
CREATE INDEX customer_promise_customer_idx       ON nexora.customer_promise (customer_id);

-- ===================== 2. Litige client (§25) ==========================
CREATE TABLE nexora.customer_dispute (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid        NOT NULL REFERENCES nexora.company(id),
    customer_id     uuid        NOT NULL,
    receivable_id   uuid,
    disputed_amount nexora.money_amount NOT NULL,
    reason          text        NOT NULL,
    status          text        NOT NULL DEFAULT 'open',
    resolution      text,
    resolved_at     timestamptz,
    resolved_by     uuid REFERENCES nexora.app_user(id),
    created_by      uuid REFERENCES nexora.app_user(id),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_dispute_amount_chk CHECK (disputed_amount > 0),
    CONSTRAINT customer_dispute_status_chk CHECK (status IN (
        'open','under_review','resolved_customer','resolved_company','cancelled')),
    CONSTRAINT customer_dispute_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT customer_dispute_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT customer_dispute_receivable_fk FOREIGN KEY (receivable_id, company_id)
        REFERENCES nexora.customer_receivable (id, company_id)
);
CREATE INDEX customer_dispute_company_status_idx ON nexora.customer_dispute (company_id, status);
CREATE INDEX customer_dispute_customer_idx       ON nexora.customer_dispute (customer_id);

-- ===================== 3. Blocage client (§26-27) ======================
CREATE TABLE nexora.customer_block (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL REFERENCES nexora.company(id),
    customer_id    uuid        NOT NULL,
    block_type     text        NOT NULL DEFAULT 'credit',
    reason         text        NOT NULL,
    blocked_at     timestamptz NOT NULL DEFAULT now(),
    blocked_by     uuid REFERENCES nexora.app_user(id),
    released_at    timestamptz,
    released_by    uuid REFERENCES nexora.app_user(id),
    release_reason text,
    is_active      boolean GENERATED ALWAYS AS (released_at IS NULL) STORED,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_block_type_chk CHECK (block_type IN ('credit','sales','delivery','account')),
    CONSTRAINT customer_block_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT customer_block_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id)
);
CREATE UNIQUE INDEX customer_block_active_uk
    ON nexora.customer_block (customer_id, block_type) WHERE released_at IS NULL;
CREATE INDEX customer_block_customer_idx ON nexora.customer_block (customer_id);

-- ===================== 4. Radiation de créance (§37) ===================
CREATE TABLE nexora.receivable_write_off (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid        NOT NULL REFERENCES nexora.company(id),
    receivable_id   uuid        NOT NULL,
    customer_id     uuid        NOT NULL,
    amount          nexora.money_amount NOT NULL,
    reason          text        NOT NULL,
    journal_entry_id uuid,
    approved_by     uuid REFERENCES nexora.app_user(id),
    written_off_at  timestamptz NOT NULL DEFAULT now(),
    created_by      uuid REFERENCES nexora.app_user(id),
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT receivable_write_off_amount_chk CHECK (amount > 0),
    CONSTRAINT receivable_write_off_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT receivable_write_off_receivable_fk FOREIGN KEY (receivable_id, company_id)
        REFERENCES nexora.customer_receivable (id, company_id),
    CONSTRAINT receivable_write_off_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id)
);
CREATE INDEX receivable_write_off_company_idx ON nexora.receivable_write_off (company_id, written_off_at DESC);

-- ===================== 5. Statuts créance §13 ==========================
ALTER TABLE nexora.customer_receivable DROP CONSTRAINT IF EXISTS customer_receivable_status_chk;
ALTER TABLE nexora.customer_receivable
    ADD CONSTRAINT customer_receivable_status_chk CHECK (status IN (
        'OPEN','PARTIALLY_PAID','PAID','OVERDUE','IN_DISPUTE','WRITTEN_OFF','CANCELLED'));

-- ===================== 6. Mapping comptable : créances irrécouvrables ==
ALTER TABLE nexora.account_mapping DROP CONSTRAINT account_mapping_key_chk;
ALTER TABLE nexora.account_mapping ADD CONSTRAINT account_mapping_key_chk CHECK (mapping_key IN (
    'sales_revenue','sales_tax_output','accounts_receivable','accounts_payable',
    'purchases','inventory','cogs','purchase_tax_input','expense_default',
    'cash_short_over','fx_gain','fx_loss','rounding','bank_charges','suspense',
    'cash','bank','mobile_money','retained_earnings',
    'inventory_adjustment','inventory_loss','goods_in_transit','purchase_price_variance',
    'sales_returns','store_credit','tips_payable','bad_debt'));

-- ===================== 7. RLS (§34) & audit (§48) ======================
SELECT nexora.enable_company_rls('nexora.customer_promise'::regclass);
SELECT nexora.enable_company_rls('nexora.customer_dispute'::regclass);
SELECT nexora.enable_company_rls('nexora.customer_block'::regclass);
SELECT nexora.enable_company_rls('nexora.receivable_write_off'::regclass);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['customer_promise','customer_dispute','customer_block','receivable_write_off'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 8. Permissions (§46-47) =========================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('credit.set_limit',      'Crédit — définir la limite / les conditions', 'credit', 'set_limit'),
    ('credit.collect',        'Crédit — recouvrement (relance, promesse)',   'credit', 'collect'),
    ('credit.dispute',        'Crédit — gérer les litiges',                  'credit', 'dispute'),
    ('credit.block',          'Crédit — bloquer / débloquer un client',      'credit', 'block'),
    ('credit.write_off',      'Crédit — radier une créance',                 'credit', 'write_off'),
    ('credit.view_portfolio', 'Crédit — voir le portefeuille & l''aging',    'credit', 'view_portfolio')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v40(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v40('super_administrator', ARRAY['credit.%']);
SELECT pg_temp.grant_perms_v40('group_administrator', ARRAY['credit.%']);
SELECT pg_temp.grant_perms_v40('owner_direction',     ARRAY['credit.%']);
SELECT pg_temp.grant_perms_v40('manager',             ARRAY['credit.%']);
SELECT pg_temp.grant_perms_v40('chief_accountant',    ARRAY['credit.%']);
SELECT pg_temp.grant_perms_v40('senior_accountant',   ARRAY['credit.collect','credit.dispute','credit.view_portfolio']);
SELECT pg_temp.grant_perms_v40('accountant',          ARRAY['credit.collect','credit.view_portfolio']);
SELECT pg_temp.grant_perms_v40('salesperson',         ARRAY['credit.collect','credit.view_portfolio']);

SELECT nexora.attach_common_triggers();
