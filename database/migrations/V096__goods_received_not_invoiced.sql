-- =============================================================================
-- NEXORA — V096 — Compte 4081 « Fournisseurs — factures non parvenues » (décision du 2026-10-09)
--
-- Une réception de marchandises augmentait le stock sans écriture : le compte 3110 restait en retard sur la valeur
-- du stock tant que la facture fournisseur n'était pas saisie (défaut révélé par la suite E2E, phase 19).
-- Désormais : réception D 3110 / C 4081 au coût de réception ; facture D 4081 (coût de réception) + écart de prix en
-- charge / C 4011 ; retour avant facture D 4081 / C 3110.
--
-- Cette migration crée le compte 4081 et la correspondance goods_received_not_invoiced pour chaque entreprise, les
-- ajoute à l'initialisation des nouvelles entreprises, et rattache le 4081 au compte de groupe du 4011 quand un plan
-- de groupe existe. Aucune écriture passée n'est modifiée : les réceptions antérieures restent sans écriture et leurs
-- factures gardent l'ancien traitement (voir PurchasingService.StockPartAsync).
-- Idempotente.
-- =============================================================================
SET search_path TO nexora, public;

ALTER TABLE nexora.account_mapping DROP CONSTRAINT IF EXISTS account_mapping_key_chk;
ALTER TABLE nexora.account_mapping ADD CONSTRAINT account_mapping_key_chk CHECK (mapping_key IN (
    'sales_revenue','sales_tax_output','accounts_receivable','accounts_payable',
    'purchases','inventory','cogs','purchase_tax_input','expense_default',
    'cash_short_over','fx_gain','fx_loss','rounding','bank_charges','suspense',
    'cash','bank','mobile_money','retained_earnings','current_year_earnings',
    'inventory_adjustment','inventory_loss','goods_in_transit','purchase_price_variance',
    'sales_returns','store_credit','tips_payable','bad_debt',
    -- Paie (V076)
    'salary_expense','employer_charges_expense','salaries_payable','employee_advances','employee_loans',
    'payroll_withholdings','payroll_other_deductions','employee_reimbursements_payable',
    -- Achats (V096)
    'goods_received_not_invoiced'));

CREATE OR REPLACE FUNCTION nexora.bootstrap_grni_defaults(p_company uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_chart uuid; v_group uuid;
BEGIN
    SELECT id INTO v_chart FROM nexora.chart_of_accounts WHERE company_id = p_company AND status = 'active'
     ORDER BY created_at LIMIT 1;
    IF v_chart IS NULL THEN
        RETURN;
    END IF;

    INSERT INTO nexora.account (chart_of_accounts_id, company_id, code, name, account_type)
    VALUES (v_chart, p_company, '4081', 'Fournisseurs — factures non parvenues', 'liability')
    ON CONFLICT (chart_of_accounts_id, code) DO NOTHING;

    INSERT INTO nexora.account_mapping (company_id, mapping_key, account_id)
    SELECT p_company, 'goods_received_not_invoiced', a.id
    FROM nexora.account a WHERE a.chart_of_accounts_id = v_chart AND a.code = '4081'
    ON CONFLICT (company_id, mapping_key) DO NOTHING;

    -- Plan de groupe : le 4081 suit le rattachement du 4011 (dettes fournisseurs), sinon la consolidation le
    -- signalerait comme « compte non mappé ».
    SELECT group_id INTO v_group FROM nexora.company WHERE id = p_company;
    IF v_group IS NOT NULL THEN
        INSERT INTO nexora.account_group_mapping (group_id, company_id, account_id, group_account_id, effective_from, status)
        SELECT v_group, p_company, grni.id, m.group_account_id, DATE '2000-01-01', 'active'
        FROM nexora.account grni
        JOIN nexora.account ap ON ap.chart_of_accounts_id = grni.chart_of_accounts_id AND ap.code = '4011'
        JOIN nexora.account_group_mapping m ON m.account_id = ap.id AND m.status = 'active' AND m.group_id = v_group
        WHERE grni.chart_of_accounts_id = v_chart AND grni.code = '4081'
          AND NOT EXISTS (SELECT 1 FROM nexora.account_group_mapping x WHERE x.account_id = grni.id AND x.status = 'active')
        LIMIT 1;
    END IF;
END;
$$;
GRANT EXECUTE ON FUNCTION nexora.bootstrap_grni_defaults(uuid) TO nexora_app;

SELECT nexora.bootstrap_grni_defaults(c.id) FROM nexora.company c;

-- Toute entreprise initialisée ensuite (assistant d'installation, création d'entreprise) reçoit aussi le 4081.
CREATE OR REPLACE FUNCTION nexora.bootstrap_company_defaults(p_company uuid, p_currency uuid, p_year int)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    PERFORM nexora.bootstrap_company_defaults_v072(p_company, p_currency, p_year);
    PERFORM nexora.bootstrap_payroll_defaults(p_company);
    PERFORM nexora.bootstrap_payroll_group_mapping(p_company);
    PERFORM nexora.bootstrap_grni_defaults(p_company);
END;
$$;
