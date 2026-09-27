-- =============================================================================
-- NEXORA — V025 — Ventes & POS (Phase 6)
-- Sessions de caisse rattachées aux ventes, paiements multiples, rendu monnaie,
-- promotions appliquées (résultat figé), retours/remboursements, ventes en attente.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Vente : contexte POS (§14, §17, §19, §25, §34) --------------------
ALTER TABLE nexora.sale
    ADD COLUMN cash_session_id       uuid,
    ADD COLUMN amount_tendered       nexora.money_amount NOT NULL DEFAULT 0,
    ADD COLUMN change_given          nexora.money_amount NOT NULL DEFAULT 0,
    ADD COLUMN parked_reference      text,
    ADD COLUMN parked_at             timestamptz,
    ADD COLUMN credit_override_by    uuid REFERENCES nexora.app_user(id),
    ADD COLUMN credit_override_reason text,
    ADD COLUMN voided_at             timestamptz,
    ADD COLUMN void_reason           text,
    ADD COLUMN reversal_journal_entry_id uuid;

ALTER TABLE nexora.sale
    ADD CONSTRAINT sale_change_chk CHECK (change_given >= 0 AND amount_tendered >= 0),
    ADD CONSTRAINT sale_cash_session_fk FOREIGN KEY (cash_session_id, company_id)
        REFERENCES nexora.cash_session (id, company_id);
CREATE INDEX sale_cash_session_idx ON nexora.sale (cash_session_id);
CREATE UNIQUE INDEX sale_parked_reference_uk
    ON nexora.sale (company_id, parked_reference) WHERE parked_reference IS NOT NULL;

-- --- Ligne : prix d'origine avant modification manuelle (§7, §9) ------
ALTER TABLE nexora.sale_line
    ADD COLUMN original_unit_price nexora.money_amount,
    ADD COLUMN promotion_id uuid,
    ADD COLUMN unit_cost nexora.money_amount NOT NULL DEFAULT 0;

-- --- Résultat de promotion figé à la transaction (§10) --------------
ALTER TABLE nexora.coupon ADD CONSTRAINT coupon_company_id_uk UNIQUE (id, company_id);

CREATE TABLE nexora.sale_promotion (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid        NOT NULL REFERENCES nexora.company(id),
    sale_id         uuid        NOT NULL,
    sale_line_id    uuid,
    promotion_id    uuid,
    coupon_id       uuid,
    promo_type      text        NOT NULL,
    label           text        NOT NULL DEFAULT '',
    value           numeric(20,6) NOT NULL DEFAULT 0,
    discount_amount nexora.money_amount NOT NULL DEFAULT 0,
    detail          jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sale_promotion_amount_chk CHECK (discount_amount >= 0),
    CONSTRAINT sale_promotion_sale_fk FOREIGN KEY (sale_id, company_id)
        REFERENCES nexora.sale (id, company_id) ON DELETE CASCADE,
    CONSTRAINT sale_promotion_promotion_fk FOREIGN KEY (promotion_id, company_id)
        REFERENCES nexora.promotion (id, company_id),
    CONSTRAINT sale_promotion_coupon_fk FOREIGN KEY (coupon_id, company_id)
        REFERENCES nexora.coupon (id, company_id)
);
CREATE INDEX sale_promotion_sale_idx ON nexora.sale_promotion (sale_id);

-- --- Retour : moyen de remboursement + lien paiement (§22, §23) -----
ALTER TABLE nexora.sale_return
    ADD COLUMN refund_method text NOT NULL DEFAULT 'cash',
    ADD COLUMN refund_payment_id uuid,
    ADD COLUMN restocked boolean NOT NULL DEFAULT true,
    ADD COLUMN source_operation_uuid uuid,
    ADD COLUMN journal_entry_id uuid,
    ADD COLUMN cash_session_id uuid;
ALTER TABLE nexora.sale_return
    ADD CONSTRAINT sale_return_method_chk CHECK (refund_method IN ('cash','card','bank_transfer','store_credit','credit_note','original')),
    ADD CONSTRAINT sale_return_cash_session_fk FOREIGN KEY (cash_session_id, company_id)
        REFERENCES nexora.cash_session (id, company_id);
CREATE UNIQUE INDEX sale_return_source_op_uk
    ON nexora.sale_return (source_operation_uuid) WHERE source_operation_uuid IS NOT NULL;

ALTER TABLE nexora.sale_return_line
    ADD COLUMN lot_id uuid,
    ADD COLUMN unit_cost nexora.money_amount NOT NULL DEFAULT 0,
    ADD COLUMN tax_amount nexora.money_amount NOT NULL DEFAULT 0;
ALTER TABLE nexora.sale_return_line
    ADD CONSTRAINT sale_return_line_lot_fk FOREIGN KEY (lot_id, company_id)
        REFERENCES nexora.lot (id, company_id);

-- --- account_mapping : clé ventes/retours (§22, §39) --------------
ALTER TABLE nexora.account_mapping DROP CONSTRAINT account_mapping_key_chk;
ALTER TABLE nexora.account_mapping ADD CONSTRAINT account_mapping_key_chk CHECK (mapping_key IN (
    'sales_revenue','sales_tax_output','accounts_receivable','accounts_payable',
    'purchases','inventory','cogs','purchase_tax_input','expense_default',
    'cash_short_over','fx_gain','fx_loss','rounding','bank_charges','suspense',
    'cash','bank','mobile_money','retained_earnings',
    'inventory_adjustment','inventory_loss','goods_in_transit','purchase_price_variance',
    'sales_returns','store_credit','tips_payable'));

-- --- Permissions POS (§41) ------------------------------------
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('pos.sell',            'POS — vendre',                       'pos', 'sell'),
    ('pos.change_price',    'POS — modifier un prix',             'pos', 'change_price'),
    ('pos.sell_credit',     'POS — vendre à crédit',              'pos', 'sell_credit'),
    ('pos.credit_override', 'POS — autoriser un dépassement de crédit', 'pos', 'credit_override'),
    ('pos.return',          'POS — retour',                       'pos', 'return'),
    ('pos.refund',          'POS — rembourser',                   'pos', 'refund'),
    ('pos.void_sale',       'POS — annuler une vente validée',    'pos', 'void_sale'),
    ('pos.reprint',         'POS — réimprimer un reçu',           'pos', 'reprint'),
    ('pos.open_session',    'POS — ouvrir la caisse',             'pos', 'open_session'),
    ('pos.cash_in_out',     'POS — retrait / dépôt de caisse',    'pos', 'cash_in_out'),
    ('pos.view_customer_balance', 'POS — voir le solde client',   'pos', 'view_customer_balance')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v25(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v25('super_administrator', ARRAY['pos.%']);
SELECT pg_temp.grant_perms_v25('group_administrator', ARRAY['pos.%']);
SELECT pg_temp.grant_perms_v25('owner_direction',     ARRAY['pos.%']);
SELECT pg_temp.grant_perms_v25('manager',             ARRAY['pos.%']);
SELECT pg_temp.grant_perms_v25('cashier', ARRAY[
    'pos.sell','pos.sell_credit','pos.return','pos.reprint','pos.open_session',
    'pos.cash_in_out','pos.close_session','pos.view_customer_balance']);
SELECT pg_temp.grant_perms_v25('salesperson', ARRAY['pos.sell','pos.sell_credit','pos.view_customer_balance']);

SELECT nexora.attach_common_triggers();
