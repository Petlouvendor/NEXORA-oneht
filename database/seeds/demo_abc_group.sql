-- =============================================================================
-- NEXORA — Jeu de démonstration « ABC GROUP »
-- Données PERSISTANTES pour le développement (front / back / API).
-- Prérequis : migrations V001..V021 appliquées.
-- Rejouable : supprime puis recrée le groupe de démo.
--
--   PGPASSWORD=... psql -h localhost -U postgres -d nexora -v ON_ERROR_STOP=1 -f demo_abc_group.sql
-- =============================================================================
\set ON_ERROR_STOP on
SET search_path TO nexora, public;

BEGIN;
SET LOCAL nexora.bypass_rls = 'on';

-- Garde-fou (audit 2026-10 éd. 2) : ce jeu crée des comptes au mot de passe connu (Nexora!2026). Refus sur une base qui
-- contient déjà des entreprises réelles ; pour forcer : PGOPTIONS="-c nexora.allow_demo_seed=on".
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM nexora.company WHERE id::text NOT LIKE 'dce00000-%')
       AND current_setting('nexora.allow_demo_seed', true) IS DISTINCT FROM 'on' THEN
        RAISE EXCEPTION 'Jeu de démonstration refusé : la base contient déjà des entreprises réelles (comptes à mot de passe connu).';
    END IF;
END $$;

-- --- Nettoyage (feuilles → racine) ----------------------------------
SET LOCAL nexora.allow_hard_delete = 'on';

-- Plateforme SaaS (Phase 38) — avant la suppression des entreprises de démo (FK).
DO $$
DECLARE cust uuid := (SELECT id FROM nexora.platform_customer WHERE customer_ref = 'NXC-DEMO01');
BEGIN
    IF cust IS NOT NULL THEN
        DELETE FROM nexora.billing_event            WHERE customer_id = cust;
        DELETE FROM nexora.platform_payment         WHERE customer_id = cust;
        DELETE FROM nexora.platform_payment_method  WHERE customer_id = cust;
        DELETE FROM nexora.platform_invoice_line    WHERE invoice_id IN (SELECT id FROM nexora.platform_invoice WHERE customer_id = cust);
        DELETE FROM nexora.platform_invoice         WHERE customer_id = cust;
        DELETE FROM nexora.subscription_addon       WHERE subscription_id IN (SELECT id FROM nexora.subscription WHERE customer_id = cust);
        DELETE FROM nexora.platform_license         WHERE customer_id = cust;
        DELETE FROM nexora.subscription             WHERE customer_id = cust;
        DELETE FROM nexora.support_access_grant     WHERE customer_id = cust;
        DELETE FROM nexora.support_ticket_message   WHERE ticket_id IN (SELECT id FROM nexora.support_ticket WHERE customer_id = cust);
        DELETE FROM nexora.support_ticket           WHERE customer_id = cust;
        DELETE FROM nexora.platform_customer_company WHERE customer_id = cust;
        DELETE FROM nexora.platform_customer        WHERE id = cust;
    END IF;
    DELETE FROM nexora.platform_plan  WHERE code LIKE 'demo-%';
    DELETE FROM nexora.platform_addon WHERE code LIKE 'demo-%';
END $$;
DELETE FROM nexora.user_role WHERE user_id IN (SELECT id FROM nexora.app_user WHERE email LIKE '%@nexora.demo');
DELETE FROM nexora.access_scope WHERE id = 'dce00000-0000-0000-0000-0000000000e9';
DELETE FROM nexora.app_user WHERE email LIKE '%@nexora.demo';

DO $$
DECLARE
    g uuid := 'dce00000-0000-0000-0000-000000000001';
    cos uuid[] := (SELECT array_agg(id) FROM nexora.company WHERE group_id = g);
    t text;
BEGIN
    IF cos IS NULL THEN RETURN; END IF;
    DELETE FROM nexora.user_role WHERE scope_id IN (
        SELECT id FROM nexora.access_scope WHERE group_id = g OR company_id = ANY(cos));
    DELETE FROM nexora.access_scope WHERE group_id = g OR company_id = ANY(cos);
    -- Tables portées par le groupe (group_id, pas company_id) — Phase 9.
    DELETE FROM nexora.consolidation_run_line   WHERE group_id = g;
    DELETE FROM nexora.consolidation_run_company WHERE group_id = g;
    DELETE FROM nexora.consolidation_run        WHERE group_id = g;
    DELETE FROM nexora.intercompany_match       WHERE group_id = g;
    DELETE FROM nexora.intercompany_transaction WHERE group_id = g;
    DELETE FROM nexora.group_task               WHERE group_id = g;
    DELETE FROM nexora.consolidation_adjustment WHERE group_id = g;
    DELETE FROM nexora.consolidation_scope      WHERE group_id = g;
    DELETE FROM nexora.consolidation_profile    WHERE group_id = g;
    DELETE FROM nexora.group_policy             WHERE group_id = g;
    FOREACH t IN ARRAY ARRAY[
        'automation_run','automation','workflow_run','workflow','config_version','dunning_policy','setting',
        'company_branding',
        'webhook_event','outbound_webhook_delivery','payment_intent','outbound_webhook','integration_connector',
        'fraud_signal','fraud_rule_setting','security_policy',
        'approval_action','approval_request','approval_policy','alert_rule',
        'sync_conflict','sync_operation','sync_batch','sync_device','offline_policy',
        'sale_line_modifier','sale_service_context','restaurant_table','restaurant_area',
        'recipe_ingredient','recipe','custom_field_value','custom_field','terminology_override',
        'company_module','company_activity','account_group_mapping',
        'customer_promise','customer_dispute','customer_block','receivable_write_off',
        'financial_close','treasury_suspense_item','accounting_year_close','analytics_snapshot',
        'config_transfer','sector_profile',
        'notification_outbox','notification_preference','message_template',
        'api_key',
        'golive_signoff',
        'restore_run','continuity_incident','backup','backup_policy',
        'document_send','issued_document','quote_line','quote',
        'journal_entry_line','journal_entry','payment_allocation','customer_receivable',
        'supplier_payable','sale_payment','payment','financial_transaction',
        'sale_promotion','sale_payment','sale_return_line','sale_return','sale_line','sale',
        'sales_order_line','sales_order',
        'supplier_return_line','supplier_return','supplier_invoice_line','supplier_invoice',
        'goods_receipt_line','goods_receipt','purchase_order_line','purchase_order',
        'purchase_request_line','purchase_request',
        'stock_transfer_line','stock_transfer','inventory_count_line','inventory_count',
        'stock_adjustment_line','stock_adjustment','stock_movement','lot_stock','lot','stock_balance',
        'cash_movement','cash_session','cash_register','payment_method','financial_account',
        'document_sequence','account_mapping','accounting_period','journal','account','chart_of_accounts',
        'product_cost_history','product_reorder_rule','unit_conversion','product_barcode',
        'product_supplier','product_price','price_list','pharmacy_product','product','product_category',
        'customer','supplier','tax','unit','inventory_location','pos_terminal','store',
        'warehouse','establishment'
    ] LOOP
        EXECUTE format('DELETE FROM nexora.%I WHERE company_id = ANY($1)', t) USING cos;
    END LOOP;
    DELETE FROM nexora.group_account WHERE group_id = g;
    DELETE FROM nexora.company WHERE id = ANY(cos);
END $$;
DELETE FROM nexora.exchange_rate WHERE source = 'demo';
DELETE FROM nexora.app_user      WHERE email LIKE '%@abc-group.demo';
DELETE FROM nexora.business_group WHERE id = 'dce00000-0000-0000-0000-000000000001';

-- --- Groupe + entreprises (§8 design system) -----------------------
INSERT INTO nexora.business_group (id, name, legal_name, default_currency_id)
SELECT 'dce00000-0000-0000-0000-000000000001', 'ABC GROUP', 'ABC Holding S.A.', id
FROM nexora.currency WHERE code = 'HTG';

INSERT INTO nexora.company (id, group_id, name, legal_name, main_activity_type, complexity_level, deployment_mode, default_currency_id)
SELECT v.id, 'dce00000-0000-0000-0000-000000000001', v.name, v.legal_name, v.activity, 'STANDARD', 'HYBRID',
       (SELECT id FROM nexora.currency WHERE code = 'HTG')
FROM (VALUES
    ('dce00000-0000-0000-0000-0000000000c1'::uuid, 'Pharmacie ABC',   'Pharmacie ABC S.A.',   'pharmacy'),
    ('dce00000-0000-0000-0000-0000000000c2'::uuid, 'Restaurant ABC',  'Restaurant ABC S.A.',  'restaurant'),
    ('dce00000-0000-0000-0000-0000000000c3'::uuid, 'Supermarché ABC', 'Supermarché ABC S.A.', 'supermarket')
) AS v(id, name, legal_name, activity);

-- --- Utilisateurs de démo -----------------------------------------
-- Mot de passe commun : « Nexora!2026 » (hash bcrypt via pgcrypto — dev uniquement).
-- MFA désactivée à l'ensemencement : elle s'active par enrôlement TOTP (Phase 33 §8). La
-- politique de sécurité la marque « recommandée » pour la direction / le comptable en chef.
INSERT INTO nexora.app_user (id, email, password_hash, full_name, mfa_enabled) VALUES
 ('dce00000-0000-0000-0000-0000000000d1', 'direction@abc-group.demo', crypt('Nexora!2026', gen_salt('bf', 12)), 'Direction ABC',     false),
 ('dce00000-0000-0000-0000-0000000000d2', 'compta@abc-group.demo',    crypt('Nexora!2026', gen_salt('bf', 12)), 'Comptable en chef', false),
 ('dce00000-0000-0000-0000-0000000000d3', 'caisse@abc-group.demo',    crypt('Nexora!2026', gen_salt('bf', 12)), 'Caissier Pharmacie',false);

INSERT INTO nexora.access_scope (id, scope_type, group_id) VALUES
 ('dce00000-0000-0000-0000-0000000000e1', 'GROUP', 'dce00000-0000-0000-0000-000000000001');
INSERT INTO nexora.access_scope (id, scope_type, company_id) VALUES
 ('dce00000-0000-0000-0000-0000000000e2', 'COMPANY', 'dce00000-0000-0000-0000-0000000000c1');

INSERT INTO nexora.user_role (user_id, role_id, scope_id)
SELECT 'dce00000-0000-0000-0000-0000000000d1', id, 'dce00000-0000-0000-0000-0000000000e1'
FROM nexora.role WHERE name = 'owner_direction' AND system_role;
INSERT INTO nexora.user_role (user_id, role_id, scope_id)
SELECT 'dce00000-0000-0000-0000-0000000000d2', id, 'dce00000-0000-0000-0000-0000000000e1'
FROM nexora.role WHERE name = 'chief_accountant' AND system_role;
INSERT INTO nexora.user_role (user_id, role_id, scope_id)
SELECT 'dce00000-0000-0000-0000-0000000000d3', id, 'dce00000-0000-0000-0000-0000000000e2'
FROM nexora.role WHERE name = 'cashier' AND system_role;

-- --- Paramétrage minimal de la Pharmacie ABC ---------------------
\set pharma '''dce00000-0000-0000-0000-0000000000c1'''

INSERT INTO nexora.establishment (id, company_id, name, code)
VALUES ('dce00000-0000-0000-0000-0000000000f1', :pharma, 'Pharmacie ABC — Delmas', 'DELMAS');
INSERT INTO nexora.store (id, establishment_id, company_id, name, code)
VALUES ('dce00000-0000-0000-0000-00000000a001', 'dce00000-0000-0000-0000-0000000000f1', :pharma, 'Comptoir', 'C1');
INSERT INTO nexora.pos_terminal (id, store_id, company_id, name, code)
VALUES ('dce00000-0000-0000-0000-00000000a002', 'dce00000-0000-0000-0000-00000000a001', :pharma, 'Caisse 1', 'T1');
INSERT INTO nexora.inventory_location (id, company_id, establishment_id, store_id, name, location_type)
VALUES ('dce00000-0000-0000-0000-00000000a003', :pharma, 'dce00000-0000-0000-0000-0000000000f1',
        'dce00000-0000-0000-0000-00000000a001', 'Officine', 'store');

INSERT INTO nexora.unit (id, company_id, name, code) VALUES
 ('dce00000-0000-0000-0000-00000000a011', :pharma, 'Boîte', 'BTE'),
 ('dce00000-0000-0000-0000-00000000a012', :pharma, 'Comprimé', 'CMP');
INSERT INTO nexora.tax (id, company_id, name, code, rate, type, is_inclusive) VALUES
 ('dce00000-0000-0000-0000-00000000a021', :pharma, 'Exonéré', 'EX0', 0, 'none', false),
 ('dce00000-0000-0000-0000-00000000a022', :pharma, 'TVA 10 %', 'TVA10', 10, 'vat', false);

INSERT INTO nexora.product_category (id, company_id, name) VALUES
 ('dce00000-0000-0000-0000-00000000a031', :pharma, 'Antalgiques'),
 ('dce00000-0000-0000-0000-00000000a032', :pharma, 'Antibiotiques');

INSERT INTO nexora.product (id, company_id, sku, barcode, name, category_id, unit_id, product_type, track_lots) VALUES
 ('dce00000-0000-0000-0000-00000000a041', :pharma, 'PARA500', '3401011', 'Paracétamol 500 mg — 20 cp',
   'dce00000-0000-0000-0000-00000000a031', 'dce00000-0000-0000-0000-00000000a011', 'stockable', true),
 ('dce00000-0000-0000-0000-00000000a042', :pharma, 'AMOX1G', '3401022', 'Amoxicilline 1 g — 12 cp',
   'dce00000-0000-0000-0000-00000000a032', 'dce00000-0000-0000-0000-00000000a011', 'stockable', true);

INSERT INTO nexora.pharmacy_product (product_id, company_id, active_ingredient, dosage, form, requires_prescription) VALUES
 ('dce00000-0000-0000-0000-00000000a041', :pharma, 'Paracétamol', '500 mg', 'comprimé', false),
 ('dce00000-0000-0000-0000-00000000a042', :pharma, 'Amoxicilline', '1 g', 'comprimé', true);

-- Codes commerciaux : PLU produit pesé + code-barres secondaire (Phase 23 §5-6).
UPDATE nexora.product SET plu_code = '2041', short_name = 'Paracétamol 500'
 WHERE id = 'dce00000-0000-0000-0000-00000000a041';
INSERT INTO nexora.product_barcode (product_id, company_id, barcode, barcode_type, is_primary) VALUES
 ('dce00000-0000-0000-0000-00000000a041', :pharma, '3401011', 'ean13', true),
 ('dce00000-0000-0000-0000-00000000a041', :pharma, '00003401011999', 'ean13', false);

-- Liste tarifaire « Grossiste » (Phase 23 §18).
INSERT INTO nexora.price_list (id, company_id, code, name, list_type, priority) VALUES
 ('dce00000-0000-0000-0000-00000000a025', :pharma, 'PRO', 'Grossiste', 'wholesale', 10);

INSERT INTO nexora.product_price (product_id, company_id, price_type, currency_id, amount, quantity_min, price_list_id, tax_included) VALUES
 ('dce00000-0000-0000-0000-00000000a041', :pharma, 'sale', (SELECT id FROM nexora.currency WHERE code='HTG'), 75, 0, NULL, false),
 ('dce00000-0000-0000-0000-00000000a041', :pharma, 'sale', (SELECT id FROM nexora.currency WHERE code='HTG'), 65, 10, 'dce00000-0000-0000-0000-00000000a025', false),
 ('dce00000-0000-0000-0000-00000000a041', :pharma, 'sale', (SELECT id FROM nexora.currency WHERE code='USD'), 0.55, 0, NULL, false),
 ('dce00000-0000-0000-0000-00000000a042', :pharma, 'sale', (SELECT id FROM nexora.currency WHERE code='HTG'), 250, 0, NULL, false);

INSERT INTO nexora.customer (id, company_id, customer_number, name, credit_limit, default_currency_id) VALUES
 ('dce00000-0000-0000-0000-00000000b001', :pharma, 'CLI-001', 'Clinique Bon Samaritain', 500000,
   (SELECT id FROM nexora.currency WHERE code='HTG'));
INSERT INTO nexora.supplier (id, company_id, supplier_number, name, default_currency_id) VALUES
 ('dce00000-0000-0000-0000-00000000b101', :pharma, 'FRN-001', 'Distrimed S.A.',
   (SELECT id FROM nexora.currency WHERE code='HTG')),
 ('dce00000-0000-0000-0000-00000000b102', :pharma, 'FRN-002', 'PharmaPlus Import',
   (SELECT id FROM nexora.currency WHERE code='HTG'));

INSERT INTO nexora.warehouse (id, establishment_id, company_id, name, code, type)
VALUES ('dce00000-0000-0000-0000-0000000000f2', 'dce00000-0000-0000-0000-0000000000f1', :pharma, 'Réserve centrale', 'W1', 'general');
INSERT INTO nexora.inventory_location (id, company_id, establishment_id, warehouse_id, name, location_type)
VALUES ('dce00000-0000-0000-0000-00000000a004', :pharma, 'dce00000-0000-0000-0000-0000000000f1',
        'dce00000-0000-0000-0000-0000000000f2', 'Réserve', 'warehouse');

INSERT INTO nexora.financial_account (id, company_id, account_type, name, currency_id, opening_balance, current_balance) VALUES
 ('dce00000-0000-0000-0000-00000000a051', :pharma, 'cash', 'Caisse officine',  (SELECT id FROM nexora.currency WHERE code='HTG'), 45000, 45000),
 ('dce00000-0000-0000-0000-00000000a052', :pharma, 'bank', 'Compte Unibank',    (SELECT id FROM nexora.currency WHERE code='USD'), 3200, 3200),
 ('dce00000-0000-0000-0000-00000000a053', :pharma, 'mobile_money', 'Compte prestataire Mobile Money', (SELECT id FROM nexora.currency WHERE code='HTG'), 12000, 12000);

INSERT INTO nexora.cash_register (id, company_id, store_id, financial_account_id, name, code, currency_id)
VALUES ('dce00000-0000-0000-0000-00000000a061', :pharma, 'dce00000-0000-0000-0000-00000000a001',
        'dce00000-0000-0000-0000-00000000a051', 'Caisse officine', 'R1', (SELECT id FROM nexora.currency WHERE code='HTG'));

INSERT INTO nexora.chart_of_accounts (id, company_id, name, status)
VALUES ('dce00000-0000-0000-0000-00000000a071', :pharma, 'Plan comptable Pharmacie ABC', 'active');
INSERT INTO nexora.account (chart_of_accounts_id, company_id, code, name, account_type) VALUES
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '1011', 'Capital',              'equity'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '1200', 'Report à nouveau',     'equity'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '1300', 'Résultat de l''exercice', 'equity'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '3110', 'Stocks marchandises',  'asset'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '4011', 'Fournisseurs',         'liability'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '4111', 'Clients',              'asset'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '4456', 'TVA déductible',       'asset'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '4457', 'TVA collectée',        'liability'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '4471', 'Compte d''attente',    'asset'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '5711', 'Caisse',               'asset'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '5121', 'Banque',               'asset'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '6011', 'Achats marchandises',  'expense'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '6031', 'Coût des marchandises vendues', 'expense'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '6270', 'Frais bancaires',      'expense'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '6580', 'Écart de caisse (charge)', 'expense'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '6660', 'Perte de change',      'expense'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '6800', 'Charges diverses',     'expense'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '7011', 'Ventes marchandises',  'revenue'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '7019', 'Retours et remises sur ventes', 'revenue'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '7580', 'Écart de caisse (produit)', 'revenue'),
 ('dce00000-0000-0000-0000-00000000a071', :pharma, '7660', 'Gain de change',       'revenue');

INSERT INTO nexora.payment_method (id, company_id, name, type, currency_id, requires_reference) VALUES
 ('dce00000-0000-0000-0000-00000000a081', :pharma, 'Espèces HTG', 'cash',
   (SELECT id FROM nexora.currency WHERE code='HTG'), false),
 ('dce00000-0000-0000-0000-00000000a082', :pharma, 'Carte', 'card',
   (SELECT id FROM nexora.currency WHERE code='HTG'), true);

-- Comptes GL des comptes financiers (§9 : le tender détermine le compte)
UPDATE nexora.financial_account fa SET ledger_account_id = a.id
FROM nexora.account a
WHERE a.company_id = :pharma AND a.chart_of_accounts_id = 'dce00000-0000-0000-0000-00000000a071'
  AND ((fa.id = 'dce00000-0000-0000-0000-00000000a051' AND a.code = '5711')
    OR (fa.id = 'dce00000-0000-0000-0000-00000000a052' AND a.code = '5121')
    OR (fa.id = 'dce00000-0000-0000-0000-00000000a053' AND a.code = '5121'));

-- Mapping comptable des événements (Phase 4 §5)
INSERT INTO nexora.account_mapping (company_id, mapping_key, account_id)
SELECT :pharma, m.key, a.id
FROM (VALUES
    ('sales_revenue','7011'), ('sales_tax_output','4457'), ('accounts_receivable','4111'),
    ('accounts_payable','4011'), ('purchases','6011'), ('inventory','3110'), ('cogs','6031'),
    ('purchase_tax_input','4456'), ('expense_default','6800'), ('cash_short_over','6580'),
    ('fx_gain','7660'), ('fx_loss','6660'), ('rounding','6800'), ('bank_charges','6270'),
    ('suspense','4471'), ('cash','5711'), ('bank','5121'), ('retained_earnings','1200'),
    ('current_year_earnings','1300'),
    ('inventory_adjustment','6800'), ('inventory_loss','6580'),
    ('goods_in_transit','3110'), ('purchase_price_variance','6800'),
    ('sales_returns','7019'), ('store_credit','4471'), ('tips_payable','4471'),
    ('bad_debt','6800')
) AS m(key, code)
JOIN nexora.account a ON a.company_id = :pharma
    AND a.chart_of_accounts_id = 'dce00000-0000-0000-0000-00000000a071' AND a.code = m.code;

-- Taux de change de démonstration (§22)
INSERT INTO nexora.exchange_rate (base_currency_id, quote_currency_id, rate, effective_at, source)
SELECT u.id, h.id, 132.0, '2026-01-01 00:00Z', 'demo'
FROM nexora.currency u, nexora.currency h WHERE u.code = 'USD' AND h.code = 'HTG'
ON CONFLICT DO NOTHING;
INSERT INTO nexora.exchange_rate (base_currency_id, quote_currency_id, rate, effective_at, source)
SELECT u.id, h.id, 135.0, '2026-06-01 00:00Z', 'demo'
FROM nexora.currency u, nexora.currency h WHERE u.code = 'USD' AND h.code = 'HTG'
ON CONFLICT DO NOTHING;

INSERT INTO nexora.journal (company_id, code, name, journal_type) VALUES
 (:pharma, 'VT', 'Journal des ventes',  'sales'),
 (:pharma, 'AC', 'Journal des achats',  'purchases'),
 (:pharma, 'CA', 'Journal de caisse',   'cash'),
 (:pharma, 'BQ', 'Journal de banque',   'bank'),
 (:pharma, 'OD', 'Opérations diverses', 'general'),
 (:pharma, 'AN', 'À-nouveaux',          'opening'),
 (:pharma, 'CL', 'Clôture',             'closing');

INSERT INTO nexora.accounting_period (company_id, name, start_date, end_date, status)
VALUES (:pharma, 'Exercice 2026', '2026-01-01', '2026-12-31', 'open');

-- Séries documentaires (§19-20). `year` doit correspondre à celui passé par l'application
-- (nexora.next_document_number reçoit toujours l'année courante) — sinon le préfixe est ignoré.
INSERT INTO nexora.document_sequence (company_id, document_type, year, prefix, include_year) VALUES
 (:pharma, 'sale',                   2026, 'VTE', true),
 (:pharma, 'supplier_invoice',       2026, 'FAF', true),
 (:pharma, 'payment',                2026, 'RGL', true),
 (:pharma, 'goods_receipt',          2026, 'BR',  true),
 (:pharma, 'quote',                  2026, 'DEV', true),
 (:pharma, 'document:invoice',       2026, 'INV', true),
 (:pharma, 'document:deposit_invoice',2026, 'ACP', true),
 (:pharma, 'document:final_invoice', 2026, 'FAC', true),
 (:pharma, 'document:credit_note',   2026, 'AVR', true),
 (:pharma, 'document:receipt',       2026, 'RCU', true),
 (:pharma, 'document:delivery_note', 2026, 'BL',  true),
 (:pharma, 'document:proforma',      2026, 'PRO', true)
ON CONFLICT ON CONSTRAINT document_sequence_uk DO NOTHING;

-- --- Configuration sectorielle (Phase 8) --------------------------
-- Activité principale + modules recommandés pour chaque entreprise du groupe.
INSERT INTO nexora.company_activity (company_id, activity_type, is_primary)
SELECT id, main_activity_type, true FROM nexora.company
WHERE group_id = 'dce00000-0000-0000-0000-000000000001'
ON CONFLICT (company_id, activity_type) DO NOTHING;

INSERT INTO nexora.company_module (company_id, module_key)
SELECT c.id, p.module_key
FROM nexora.company c
JOIN nexora.sector_module_preset p ON p.activity_type = c.main_activity_type
WHERE c.group_id = 'dce00000-0000-0000-0000-000000000001'
ON CONFLICT (company_id, module_key) DO NOTHING;

-- Restaurant ABC : salle + deux tables prêtes pour la démo du module Restaurant.
INSERT INTO nexora.establishment (id, company_id, name, code)
VALUES ('dce00000-0000-0000-0000-0000000000f3', 'dce00000-0000-0000-0000-0000000000c2', 'Restaurant ABC — Pétion-Ville', 'PV1')
ON CONFLICT (id) DO NOTHING;
INSERT INTO nexora.restaurant_area (id, company_id, establishment_id, name, code)
VALUES ('dce00000-0000-0000-0000-00000000a201', 'dce00000-0000-0000-0000-0000000000c2',
        'dce00000-0000-0000-0000-0000000000f3', 'Salle principale', 'SALLE')
ON CONFLICT (id) DO NOTHING;
INSERT INTO nexora.restaurant_table (id, company_id, establishment_id, area_id, name, code, seats) VALUES
 ('dce00000-0000-0000-0000-00000000a211', 'dce00000-0000-0000-0000-0000000000c2',
  'dce00000-0000-0000-0000-0000000000f3', 'dce00000-0000-0000-0000-00000000a201', 'Table 1', 'T1', 4),
 ('dce00000-0000-0000-0000-00000000a212', 'dce00000-0000-0000-0000-0000000000c2',
  'dce00000-0000-0000-0000-0000000000f3', 'dce00000-0000-0000-0000-00000000a201', 'Table 2', 'T2', 2)
ON CONFLICT (id) DO NOTHING;

-- =============================================================================
-- Multi-entreprises & Consolidation (Phase 9)
-- =============================================================================
\set resto '''dce00000-0000-0000-0000-0000000000c2'''

-- Comptabilité minimale du Restaurant ABC (pour intercompany + consolidation).
INSERT INTO nexora.chart_of_accounts (id, company_id, name, status)
VALUES ('dce00000-0000-0000-0000-00000000c071', :resto, 'Plan comptable Restaurant ABC', 'active');
INSERT INTO nexora.account (chart_of_accounts_id, company_id, code, name, account_type) VALUES
 ('dce00000-0000-0000-0000-00000000c071', :resto, '1011', 'Capital',              'equity'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '1200', 'Report à nouveau',     'equity'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '1300', 'Résultat de l''exercice', 'equity'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '3110', 'Stocks',               'asset'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '4011', 'Fournisseurs',         'liability'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '4111', 'Clients',              'asset'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '5711', 'Caisse',               'asset'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '5121', 'Banque',               'asset'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '6011', 'Achats',               'expense'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '6031', 'Coût des marchandises vendues', 'expense'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '6800', 'Charges diverses',     'expense'),
 ('dce00000-0000-0000-0000-00000000c071', :resto, '7011', 'Ventes',               'revenue');
INSERT INTO nexora.journal (company_id, code, name, journal_type) VALUES
 (:resto, 'VT', 'Journal des ventes',  'sales'),
 (:resto, 'AC', 'Journal des achats',  'purchases'),
 (:resto, 'CA', 'Journal de caisse',   'cash'),
 (:resto, 'BQ', 'Journal de banque',   'bank'),
 (:resto, 'OD', 'Opérations diverses', 'general'),
 (:resto, 'AN', 'À-nouveaux',          'opening'),
 (:resto, 'CL', 'Clôture',             'closing');
INSERT INTO nexora.accounting_period (company_id, name, start_date, end_date, status)
VALUES (:resto, 'Exercice 2026', '2026-01-01', '2026-12-31', 'open');

-- Politique groupe : consolidation en HTG.
INSERT INTO nexora.group_policy (group_id, consolidation_currency_id)
SELECT 'dce00000-0000-0000-0000-000000000001', id FROM nexora.currency WHERE code = 'HTG'
ON CONFLICT (group_id) DO NOTHING;

-- --- Trésorerie : un suspens à résoudre (Phase 28 §32) -----------
INSERT INTO nexora.treasury_suspense_item
    (id, company_id, financial_account_id, kind, amount, currency_id, reference, reason, resolution_due_date, status)
VALUES ('dce00000-0000-0000-0000-00000000a091', :pharma, 'dce00000-0000-0000-0000-00000000a052',
        'bank_discrepancy', 2450, (SELECT id FROM nexora.currency WHERE code='HTG'),
        'VIR-2026-0417', 'Virement reçu sans référence de facture — à identifier',
        current_date + 5, 'open');

-- Plan comptable groupe (§12). GA-AR / GA-AP sont éliminables (§16).
INSERT INTO nexora.group_account (group_id, code, name, account_type, is_eliminable, sort_order) VALUES
 ('dce00000-0000-0000-0000-000000000001', 'GA-CAP',  'Capitaux propres',         'equity',    false, 10),
 ('dce00000-0000-0000-0000-000000000001', 'GA-RAN',  'Report à nouveau',         'equity',    false, 11),
 ('dce00000-0000-0000-0000-000000000001', 'GA-STK',  'Stocks',                   'asset',     false, 20),
 ('dce00000-0000-0000-0000-000000000001', 'GA-AR',   'Clients (dont intragroupe)','asset',     true,  21),
 ('dce00000-0000-0000-0000-000000000001', 'GA-CASH', 'Trésorerie',               'asset',     false, 22),
 ('dce00000-0000-0000-0000-000000000001', 'GA-AP',   'Fournisseurs (dont intragroupe)','liability', true, 30),
 ('dce00000-0000-0000-0000-000000000001', 'GA-SALES','Chiffre d''affaires',      'revenue',   false, 40),
 ('dce00000-0000-0000-0000-000000000001', 'GA-COGS', 'Coût des ventes',          'expense',   false, 50),
 ('dce00000-0000-0000-0000-000000000001', 'GA-EXP',  'Autres charges',           'expense',   false, 51);

-- Mapping compte local → compte groupe pour Pharmacie ABC et Restaurant ABC (§12).
INSERT INTO nexora.account_group_mapping (group_id, company_id, account_id, group_account_id, status)
SELECT 'dce00000-0000-0000-0000-000000000001', a.company_id, a.id, ga.id, 'active'
FROM nexora.account a
JOIN nexora.company c ON c.id = a.company_id AND c.group_id = 'dce00000-0000-0000-0000-000000000001'
JOIN (VALUES
    ('1011','GA-CAP'), ('1200','GA-RAN'), ('3110','GA-STK'), ('4111','GA-AR'),
    ('5711','GA-CASH'), ('5121','GA-CASH'), ('4011','GA-AP'),
    ('7011','GA-SALES'), ('7019','GA-SALES'), ('6011','GA-EXP'), ('6031','GA-COGS'),
    ('6270','GA-EXP'), ('6580','GA-EXP'), ('6660','GA-EXP'), ('6800','GA-EXP'),
    ('4456','GA-STK'), ('4457','GA-AP'), ('4471','GA-STK'), ('7580','GA-SALES'),
    ('7660','GA-SALES')
) AS m(local_code, group_code) ON m.local_code = a.code
JOIN nexora.group_account ga ON ga.group_id = 'dce00000-0000-0000-0000-000000000001' AND ga.code = m.group_code
ON CONFLICT (account_id) WHERE status = 'active' DO NOTHING;

-- =============================================================================
-- Offline & synchronisation (Phase 10)
-- =============================================================================
-- Politique offline par entreprise (défaut conservateur : comptant OK, crédit refusé).
INSERT INTO nexora.offline_policy (company_id)
SELECT id FROM nexora.company WHERE group_id = 'dce00000-0000-0000-0000-000000000001'
ON CONFLICT (company_id) DO NOTHING;

-- Pharmacie ABC : la caisse T1 est enregistrée comme appareil de synchronisation.
INSERT INTO nexora.sync_device (id, company_id, device_identifier, device_type, name, pos_terminal_id, status, last_seen_at)
VALUES ('dce00000-0000-0000-0000-00000000d301', :pharma, 'PHARMA-CAISSE-1', 'pos', 'Caisse 1 — Comptoir',
        'dce00000-0000-0000-0000-00000000a002', 'active', now())
ON CONFLICT (company_id, device_identifier) DO NOTHING;

-- =============================================================================
-- Sécurité, Audit & Anti-fraude (Phase 11)
-- =============================================================================
-- Politique de sécurité par défaut + règles anti-fraude actives pour chaque entreprise.
INSERT INTO nexora.security_policy (company_id)
SELECT id FROM nexora.company WHERE group_id = 'dce00000-0000-0000-0000-000000000001'
ON CONFLICT (company_id) DO NOTHING;

INSERT INTO nexora.fraud_rule_setting (company_id, rule_code)
SELECT c.id, fr.code
FROM nexora.company c CROSS JOIN nexora.fraud_rule fr
WHERE c.group_id = 'dce00000-0000-0000-0000-000000000001'
ON CONFLICT (company_id, rule_code) DO NOTHING;

-- Seuils d'approbation de la Pharmacie ABC : dépense ≥ 25 000, remboursement ≥ 5 000 (double), remise ≥ 3 000.
INSERT INTO nexora.approval_policy (company_id, request_type, min_amount, required_levels) VALUES
 (:pharma, 'expense',  25000, 1),
 (:pharma, 'refund',   5000,  2),
 (:pharma, 'discount', 3000,  1),
 (:pharma, 'stock_adjustment', 10000, 1),
 (:pharma, 'period_close', 0, 1)
ON CONFLICT DO NOTHING;

-- =============================================================================
-- Intégrations, Paiements & Écosystème (Phase 12)
-- =============================================================================
-- Pharmacie ABC : un connecteur de paiement Mobile Money simulé (bac à sable §42),
-- règlements crédités sur le compte prestataire dédié.
INSERT INTO nexora.integration_connector
    (id, company_id, code, name, connector_type, provider, environment, status, config, webhook_secret, credentials_ref, configured_by)
VALUES ('dce00000-0000-0000-0000-00000000e401', :pharma, 'moncash-sim', 'Mobile Money (bac à sable)',
        'payment', 'moncash-sim', 'test', 'active',
        '{"settlement_account":"dce00000-0000-0000-0000-00000000a053","callback_style":"hmac-sha256"}'::jsonb,
        'whsec_demo_pharma_moncash', 'vault://dce00000-0000-0000-0000-0000000000c1/moncash-sim',
        'dce00000-0000-0000-0000-0000000000d1')
ON CONFLICT (company_id, code) DO NOTHING;

-- Webhook sortant de démonstration : notifie un partenaire à chaque paiement confirmé.
INSERT INTO nexora.outbound_webhook
    (id, company_id, name, target_url, secret, subscribed_events, status, created_by)
VALUES ('dce00000-0000-0000-0000-00000000e402', :pharma, 'Partenaire — notifications',
        'https://sim.webhook.local/ok', 'whsec_demo_pharma_outbound',
        '["payment.confirmed","payment.refunded","sale.completed"]'::jsonb, 'active',
        'dce00000-0000-0000-0000-0000000000d1')
ON CONFLICT (id, company_id) DO NOTHING;

-- =============================================================================
-- Personnalisation UX par entreprise (Phase 13 §35) — accents validés (luminance bornée).
-- =============================================================================
INSERT INTO nexora.company_branding (company_id, display_name, accent_color, receipt_format, document_footer, configured_by) VALUES
 ('dce00000-0000-0000-0000-0000000000c1', 'Pharmacie ABC — Delmas', '#00F5D4', 'standard',
  'Merci de votre visite — Pharmacie ABC, Delmas 33', 'dce00000-0000-0000-0000-0000000000d1'),
 ('dce00000-0000-0000-0000-0000000000c2', 'Restaurant ABC',         '#FF9F45', 'detailed',
  'Restaurant ABC — service compris', 'dce00000-0000-0000-0000-0000000000d1'),
 ('dce00000-0000-0000-0000-0000000000c3', 'Supermarché ABC',        '#5B8DEF', 'compact',
  NULL, 'dce00000-0000-0000-0000-0000000000d1')
ON CONFLICT (company_id) DO NOTHING;

-- =============================================================================
-- Administration, Paramétrage & Automatisation (Phase 14)
-- =============================================================================
-- Paramètres : la Pharmacie limite la remise POS à 10 % et impose la sauvegarde.
INSERT INTO nexora.setting (key, scope, scope_id, company_id, value, note, updated_by) VALUES
 ('pos.max_discount_percent', 'company', :pharma, :pharma, '10'::jsonb,   'Politique officine', 'dce00000-0000-0000-0000-0000000000d1'),
 ('stock.enforce_fefo',       'company', :pharma, :pharma, 'true'::jsonb, 'Produits périssables — FEFO obligatoire', 'dce00000-0000-0000-0000-0000000000d1'),
 ('backup.enabled',           'company', :pharma, :pharma, 'true'::jsonb, NULL, 'dce00000-0000-0000-0000-0000000000d1')
ON CONFLICT (key, scope, scope_id) DO NOTHING;

-- Politique de relance clients à 3 niveaux.
INSERT INTO nexora.dunning_policy (id, company_id, name, channel, levels, created_by)
VALUES ('dce00000-0000-0000-0000-00000000e501', :pharma, 'Relances standard', 'email',
        '[{"level":1,"offsetDays":3,"title":"Rappel amical","message":"Votre échéance est dépassée."},
          {"level":2,"offsetDays":15,"title":"Relance","message":"Merci de régulariser sous 7 jours."},
          {"level":3,"offsetDays":30,"title":"Mise en demeure","message":"Dernier avis avant transmission."}]'::jsonb,
        'dce00000-0000-0000-0000-0000000000d1')
ON CONFLICT (company_id, name) DO NOTHING;

-- Automatisation : relance quotidienne à 08h00.
INSERT INTO nexora.automation (id, company_id, code, name, kind, schedule, parameters, status, created_by)
VALUES ('dce00000-0000-0000-0000-00000000e502', :pharma, 'daily-dunning', 'Relances clients quotidiennes', 'dunning',
        '{"frequency":"daily","atTime":"08:00","timezone":"America/Port-au-Prince"}'::jsonb, '{}'::jsonb, 'active',
        'dce00000-0000-0000-0000-0000000000d1')
ON CONFLICT (company_id, code) DO NOTHING;

-- =============================================================================
-- Administration SaaS & monétisation (Phase 38)
-- =============================================================================
-- Administrateur plateforme NEXORA (rôle à scope PLATFORM — pas d'entreprise).
INSERT INTO nexora.app_user (id, email, password_hash, full_name, mfa_enabled) VALUES
 ('dce00000-0000-0000-0000-0000000000d9', 'platform@nexora.demo', crypt('Nexora!2026', gen_salt('bf', 12)), 'Admin plateforme NEXORA', false);
INSERT INTO nexora.access_scope (id, scope_type) VALUES ('dce00000-0000-0000-0000-0000000000e9', 'PLATFORM');
INSERT INTO nexora.user_role (user_id, role_id, scope_id)
SELECT 'dce00000-0000-0000-0000-0000000000d9', id, 'dce00000-0000-0000-0000-0000000000e9'
FROM nexora.role WHERE name = 'platform_admin' AND system_role;

-- Catalogue commercial de démonstration (§4-6). Prix indicatifs en USD/mois.
INSERT INTO nexora.platform_plan (id, code, name, tier, description, price_amount, currency_code, period, trial_days, quotas, features, sort_order) VALUES
 ('dce00000-0000-0000-0000-0000000f1001', 'demo-starter', 'Starter', 'starter', 'Petite entreprise, 1 société', 19, 'USD', 'monthly', 14,
   '{"companies":1,"users":3,"establishments":1,"terminals":1,"transactions":2000,"storage_mb":1024,"api_requests":10000,"documents":500,"automations":2}'::jsonb,
   '{"pos":"included","accounting":"included","reporting":"included","group":"unavailable","integrations":"optional"}'::jsonb, 1),
 ('dce00000-0000-0000-0000-0000000f1002', 'demo-business', 'Business', 'business', 'PME multi-boutiques', 49, 'USD', 'monthly', 14,
   '{"companies":3,"users":15,"establishments":5,"terminals":10,"transactions":20000,"storage_mb":10240,"api_requests":100000,"documents":5000,"automations":10}'::jsonb,
   '{"pos":"included","accounting":"included","reporting":"included","group":"optional","integrations":"included"}'::jsonb, 2),
 ('dce00000-0000-0000-0000-0000000f1003', 'demo-professional', 'Professional', 'professional', 'Groupe et consolidation', 129, 'USD', 'monthly', 14,
   '{"companies":10,"users":50,"establishments":25,"terminals":50,"transactions":200000,"storage_mb":51200,"api_requests":1000000,"documents":50000,"automations":50}'::jsonb,
   '{"pos":"included","accounting":"included","reporting":"included","group":"included","integrations":"included"}'::jsonb, 3),
 ('dce00000-0000-0000-0000-0000000f1004', 'demo-enterprise', 'Enterprise', 'enterprise', 'Sur devis — SLA contractuel', 0, 'USD', 'monthly', 0,
   '{}'::jsonb, '{"pos":"included","accounting":"included","reporting":"included","group":"included","integrations":"included"}'::jsonb, 4);

INSERT INTO nexora.platform_addon (id, code, name, kind, quota_key, quota_delta, unit_price, currency_code) VALUES
 ('dce00000-0000-0000-0000-0000000f2001', 'demo-users-5', '5 utilisateurs supplémentaires', 'users', 'users', 5, 15, 'USD'),
 ('dce00000-0000-0000-0000-0000000f2002', 'demo-storage-10', '10 Go de stockage', 'storage', 'storage_mb', 10240, 9, 'USD');

-- Organisation cliente « ABC Holding » — propriétaire des 3 entreprises de démo (§9).
INSERT INTO nexora.platform_customer (id, customer_ref, legal_name, primary_contact_name, primary_contact_email, billing_email, country_code, billing_currency, commercial_terms, created_by) VALUES
 ('dce00000-0000-0000-0000-00000000fc01', 'NXC-DEMO01', 'ABC Holding S.A.', 'Direction ABC', 'direction@abc-group.demo', 'billing@abc-group.demo', 'HT', 'USD', 'Paiement à 14 jours', 'dce00000-0000-0000-0000-0000000000d9');
INSERT INTO nexora.platform_customer_company (customer_id, company_id)
SELECT 'dce00000-0000-0000-0000-00000000fc01', id FROM nexora.company WHERE group_id = 'dce00000-0000-0000-0000-000000000001';

-- Abonnement actif sur le plan Business, période mensuelle en cours.
INSERT INTO nexora.subscription (id, customer_id, plan_id, status, period, price_amount, currency_code,
    current_period_start, current_period_end, activated_at, created_by) VALUES
 ('dce00000-0000-0000-0000-00000000f501', 'dce00000-0000-0000-0000-00000000fc01', 'dce00000-0000-0000-0000-0000000f1002',
  'active', 'monthly', 49, 'USD', now() - interval '10 days', now() + interval '20 days', now() - interval '10 days',
  'dce00000-0000-0000-0000-0000000000d9');

-- Licence organisation active + facture de période réglée.
INSERT INTO nexora.platform_license (id, customer_id, subscription_id, scope, status, grace_days, activated_at, expires_at) VALUES
 ('dce00000-0000-0000-0000-00000000f601', 'dce00000-0000-0000-0000-00000000fc01', 'dce00000-0000-0000-0000-00000000f501',
  'organization', 'active', 7, now() - interval '10 days', now() + interval '27 days');

INSERT INTO nexora.platform_invoice (id, number, customer_id, subscription_id, kind, status, currency_code,
    issue_date, due_date, period_start, period_end, subtotal_amount, tax_amount, total_amount, paid_amount, issued_at, paid_at) VALUES
 ('dce00000-0000-0000-0000-00000000f701', 'NXR-DEMO-000001', 'dce00000-0000-0000-0000-00000000fc01', 'dce00000-0000-0000-0000-00000000f501',
  'invoice', 'paid', 'USD', (current_date - 10), (current_date + 4), (current_date - 10), (current_date + 20),
  49, 0, 49, 49, now() - interval '10 days', now() - interval '9 days');
INSERT INTO nexora.platform_invoice_line (invoice_id, line_type, description, quantity, unit_price, amount, sort_order) VALUES
 ('dce00000-0000-0000-0000-00000000f701', 'plan', 'Abonnement demo-business (monthly)', 1, 49, 49, 0);
INSERT INTO nexora.platform_payment (id, customer_id, invoice_id, method, provider, provider_ref, amount, currency_code, status, reconciled_at) VALUES
 ('dce00000-0000-0000-0000-00000000f801', 'dce00000-0000-0000-0000-00000000fc01', 'dce00000-0000-0000-0000-00000000f701',
  'card', 'simulated', 'demo_ch_0001', 49, 'USD', 'succeeded', now() - interval '9 days');

INSERT INTO nexora.billing_event (customer_id, subscription_id, event_type, payload, occurred_at) VALUES
 ('dce00000-0000-0000-0000-00000000fc01', 'dce00000-0000-0000-0000-00000000f501', 'SubscriptionCreated', '{"plan":"demo-business"}'::jsonb, now() - interval '10 days'),
 ('dce00000-0000-0000-0000-00000000fc01', 'dce00000-0000-0000-0000-00000000f501', 'InvoicePaid', '{"invoice":"NXR-DEMO-000001"}'::jsonb, now() - interval '9 days');

-- Compteurs d'usage du mois courant (§22).
INSERT INTO nexora.usage_counter (customer_id, resource, value) VALUES
 ('dce00000-0000-0000-0000-00000000fc01', 'companies', 3),
 ('dce00000-0000-0000-0000-00000000fc01', 'users', 4),
 ('dce00000-0000-0000-0000-00000000fc01', 'transactions', 4200),
 ('dce00000-0000-0000-0000-00000000fc01', 'documents', 180)
ON CONFLICT ON CONSTRAINT usage_counter_uk DO NOTHING;

COMMIT;

\echo 'Démo ABC GROUP chargée : 1 groupe, 3 entreprises, 4 utilisateurs (dont platform@nexora.demo), abonnement SaaS Business actif.'
