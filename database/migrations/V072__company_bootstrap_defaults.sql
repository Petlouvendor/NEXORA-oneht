-- =============================================================================
-- NEXORA — V072 — Résumé fonctionnel §20 : une entreprise créée doit pouvoir travailler tout de suite
-- (docs/guide-fonctionnel-conformite.md, écart RF-06).
--
-- La création d'entreprise (SqlCompanyProvisioningService) produisait un plan comptable VIDE : ni
-- comptes, ni correspondances, ni caisse, ni point de vente — la première vente échouait. Cette
-- fonction pose le référentiel minimal, calqué sur la démo (Pharmacie ABC) :
--   • plan comptable (21 comptes) + 27 correspondances utilisées par le moteur comptable ;
--   • journaux VT/AC/CA/BQ/OD/AN/CL, exercice de l'année ;
--   • taxes (exonéré, TVA 10 %), unité « Pièce » ;
--   • comptes financiers Caisse principale (5711) et Banque (5121), moyens de paiement ;
--   • succursale « Siège », point de vente, emplacement de stock, terminal, caisse enregistreuse ;
--   • préfixes de numérotation des documents de l'année.
-- Idempotente : un élément déjà présent n'est ni dupliqué ni modifié. Utilisée par l'assistant
-- d'installation et par la création d'entreprise (Phase 9 §28).
-- =============================================================================
SET search_path TO nexora, public;

CREATE OR REPLACE FUNCTION nexora.bootstrap_company_defaults(p_company uuid, p_currency uuid, p_year int)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_name      text;
    v_chart     uuid;
    v_cash      uuid;
    v_bank      uuid;
    v_est       uuid;
    v_store     uuid;
BEGIN
    SELECT name INTO v_name FROM nexora.company WHERE id = p_company;
    IF v_name IS NULL THEN
        RAISE EXCEPTION 'Entreprise % introuvable', p_company USING ERRCODE = 'no_data_found';
    END IF;

    -- ---- Plan comptable ----------------------------------------------------------------
    SELECT id INTO v_chart FROM nexora.chart_of_accounts WHERE company_id = p_company AND status = 'active';
    IF v_chart IS NULL THEN
        INSERT INTO nexora.chart_of_accounts (company_id, name, status)
        VALUES (p_company, 'Plan comptable ' || v_name, 'active') RETURNING id INTO v_chart;
    END IF;

    INSERT INTO nexora.account (chart_of_accounts_id, company_id, code, name, account_type)
    SELECT v_chart, p_company, a.code, a.name, a.account_type
    FROM (VALUES
        ('1011', 'Capital',                         'equity'),
        ('1200', 'Report à nouveau',                'equity'),
        ('1300', 'Résultat de l''exercice',          'equity'),
        ('3110', 'Stocks marchandises',             'asset'),
        ('4011', 'Fournisseurs',                    'liability'),
        ('4111', 'Clients',                         'asset'),
        ('4456', 'TVA déductible',                  'asset'),
        ('4457', 'TVA collectée',                   'liability'),
        ('4471', 'Compte d''attente',                'asset'),
        ('5121', 'Banque',                          'asset'),
        ('5711', 'Caisse',                          'asset'),
        ('6011', 'Achats marchandises',             'expense'),
        ('6031', 'Coût des marchandises vendues',   'expense'),
        ('6270', 'Frais bancaires',                 'expense'),
        ('6580', 'Écart de caisse (charge)',        'expense'),
        ('6660', 'Perte de change',                 'expense'),
        ('6800', 'Charges diverses',                'expense'),
        ('7011', 'Ventes marchandises',             'revenue'),
        ('7019', 'Retours et remises sur ventes',   'revenue'),
        ('7580', 'Écart de caisse (produit)',       'revenue'),
        ('7660', 'Gain de change',                  'revenue')
    ) AS a(code, name, account_type)
    ON CONFLICT (chart_of_accounts_id, code) DO NOTHING;

    INSERT INTO nexora.account_mapping (company_id, mapping_key, account_id)
    SELECT p_company, m.mapping_key, a.id
    FROM (VALUES
        ('accounts_payable', '4011'), ('accounts_receivable', '4111'), ('bad_debt', '6800'),
        ('bank', '5121'), ('bank_charges', '6270'), ('cash', '5711'), ('cash_short_over', '6580'),
        ('cogs', '6031'), ('current_year_earnings', '1300'), ('expense_default', '6800'),
        ('fx_gain', '7660'), ('fx_loss', '6660'), ('goods_in_transit', '3110'), ('inventory', '3110'),
        ('inventory_adjustment', '6800'), ('inventory_loss', '6580'), ('purchase_price_variance', '6800'),
        ('purchase_tax_input', '4456'), ('purchases', '6011'), ('retained_earnings', '1200'),
        ('rounding', '6800'), ('sales_returns', '7019'), ('sales_revenue', '7011'),
        ('sales_tax_output', '4457'), ('store_credit', '4471'), ('suspense', '4471'), ('tips_payable', '4471')
    ) AS m(mapping_key, code)
    JOIN nexora.account a ON a.chart_of_accounts_id = v_chart AND a.code = m.code
    ON CONFLICT (company_id, mapping_key) DO NOTHING;

    -- ---- Journaux & exercice -----------------------------------------------------------
    INSERT INTO nexora.journal (company_id, code, name, journal_type) VALUES
        (p_company, 'VT', 'Journal des ventes',  'sales'),
        (p_company, 'AC', 'Journal des achats',  'purchases'),
        (p_company, 'CA', 'Journal de caisse',   'cash'),
        (p_company, 'BQ', 'Journal de banque',   'bank'),
        (p_company, 'OD', 'Opérations diverses', 'general'),
        (p_company, 'AN', 'À-nouveaux',          'opening'),
        (p_company, 'CL', 'Clôture',             'closing')
    ON CONFLICT (company_id, code) DO NOTHING;

    IF NOT EXISTS (SELECT 1 FROM nexora.accounting_period
                   WHERE company_id = p_company
                     AND daterange(start_date, end_date, '[]') && daterange(make_date(p_year, 1, 1), make_date(p_year, 12, 31), '[]')) THEN
        INSERT INTO nexora.accounting_period (company_id, name, start_date, end_date, status)
        VALUES (p_company, 'Exercice ' || p_year, make_date(p_year, 1, 1), make_date(p_year, 12, 31), 'open');
    END IF;

    -- ---- Taxes & unités ----------------------------------------------------------------
    INSERT INTO nexora.tax (company_id, name, code, rate, type, is_inclusive) VALUES
        (p_company, 'Exonéré',  'EX0',   0,  'none', false),
        (p_company, 'TVA 10 %', 'TVA10', 10, 'vat',  false)
    ON CONFLICT (company_id, code) DO NOTHING;
    INSERT INTO nexora.unit (company_id, name, code) VALUES (p_company, 'Pièce', 'PCE')
    ON CONFLICT (company_id, code) DO NOTHING;

    -- ---- Trésorerie --------------------------------------------------------------------
    INSERT INTO nexora.financial_account (company_id, account_type, name, currency_id, ledger_account_id)
    SELECT p_company, 'cash', 'Caisse principale', p_currency,
           (SELECT id FROM nexora.account WHERE chart_of_accounts_id = v_chart AND code = '5711')
    WHERE NOT EXISTS (SELECT 1 FROM nexora.financial_account WHERE company_id = p_company AND account_type = 'cash');
    INSERT INTO nexora.financial_account (company_id, account_type, name, currency_id, ledger_account_id)
    SELECT p_company, 'bank', 'Banque', p_currency,
           (SELECT id FROM nexora.account WHERE chart_of_accounts_id = v_chart AND code = '5121')
    WHERE NOT EXISTS (SELECT 1 FROM nexora.financial_account WHERE company_id = p_company AND account_type = 'bank');
    SELECT id INTO v_cash FROM nexora.financial_account
     WHERE company_id = p_company AND account_type = 'cash' AND status = 'active' ORDER BY created_at LIMIT 1;

    IF NOT EXISTS (SELECT 1 FROM nexora.payment_method WHERE company_id = p_company) THEN
        INSERT INTO nexora.payment_method (company_id, name, type, currency_id, requires_reference) VALUES
            (p_company, 'Espèces',      'cash',         p_currency, false),
            (p_company, 'Carte',        'card',         p_currency, true),
            (p_company, 'Mobile money', 'mobile_money', p_currency, true),
            (p_company, 'Virement',     'bank_transfer', p_currency, true);
    END IF;

    -- ---- Organisation : succursale, point de vente, stock, caisse -------------------------
    SELECT id INTO v_est FROM nexora.establishment WHERE company_id = p_company ORDER BY created_at LIMIT 1;
    IF v_est IS NULL THEN
        INSERT INTO nexora.establishment (company_id, name, code)
        VALUES (p_company, 'Siège', 'SIEGE') RETURNING id INTO v_est;
    END IF;

    SELECT id INTO v_store FROM nexora.store WHERE company_id = p_company ORDER BY created_at LIMIT 1;
    IF v_store IS NULL THEN
        INSERT INTO nexora.store (establishment_id, company_id, name, code)
        VALUES (v_est, p_company, 'Point de vente principal', 'PV1') RETURNING id INTO v_store;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM nexora.inventory_location WHERE company_id = p_company AND store_id = v_store) THEN
        INSERT INTO nexora.inventory_location (company_id, establishment_id, store_id, name, location_type, code)
        SELECT p_company, s.establishment_id, v_store, 'Magasin', 'store', 'MAG'
        FROM nexora.store s WHERE s.id = v_store;
    END IF;

    INSERT INTO nexora.pos_terminal (store_id, company_id, name, code)
    VALUES (v_store, p_company, 'Terminal 1', 'T1')
    ON CONFLICT (company_id, code) DO NOTHING;

    IF NOT EXISTS (SELECT 1 FROM nexora.cash_register WHERE company_id = p_company) THEN
        INSERT INTO nexora.cash_register (company_id, store_id, financial_account_id, name, code, currency_id)
        VALUES (p_company, v_store, v_cash, 'Caisse 1', 'R1', p_currency);
    END IF;

    -- ---- Numérotation des documents de l'année ------------------------------------------
    INSERT INTO nexora.document_sequence (company_id, document_type, year, prefix, include_year)
    SELECT p_company, d.document_type, p_year, d.prefix, true
    FROM (VALUES
        ('sale', 'VTE'), ('supplier_invoice', 'FAF'), ('payment', 'RGL'), ('goods_receipt', 'BR'),
        ('quote', 'DEV'), ('document:invoice', 'INV'), ('document:final_invoice', 'FAC'),
        ('document:deposit_invoice', 'ACP'), ('document:credit_note', 'AVR'), ('document:receipt', 'RCU'),
        ('document:proforma', 'PRO'), ('document:delivery_note', 'BL')
    ) AS d(document_type, prefix)
    ON CONFLICT ON CONSTRAINT document_sequence_uk DO NOTHING;
END;
$$;

GRANT EXECUTE ON FUNCTION nexora.bootstrap_company_defaults(uuid, uuid, int) TO nexora_app;
