-- =============================================================================
-- NEXORA — Test fonctionnel du modèle de données (Phase 2)
-- Vérifie les scénarios §32 et les contraintes critiques §31 du modèle.
-- Tout est exécuté dans une transaction annulée à la fin : aucune trace.
--
--   PGPASSWORD=... psql -h localhost -U postgres -d nexora -v ON_ERROR_STOP=1 -f smoke_test.sql
-- =============================================================================
\set ON_ERROR_STOP on
SET search_path TO nexora, public;

SELECT id AS htg FROM nexora.currency WHERE code = 'HTG' \gset

BEGIN;
SET LOCAL nexora.bypass_rls = 'on';
SET LOCAL nexora.user_id = '00000000-0000-0000-0000-0000000000aa';

\echo '--- 0. Contexte de démonstration -------------------------------------'
INSERT INTO app_user (id, email, password_hash, full_name)
VALUES ('00000000-0000-0000-0000-0000000000aa', 'demo@nexora.test', 'x', 'Utilisateur démo');

INSERT INTO business_group (id, name, default_currency_id)
VALUES ('11111111-0000-0000-0000-000000000001', 'ABC GROUP', :'htg');

INSERT INTO company (id, group_id, name, main_activity_type, complexity_level, default_currency_id) VALUES
 ('22222222-0000-0000-0000-00000000000a', '11111111-0000-0000-0000-000000000001', 'Pharmacie ABC',   'pharmacy',    'STANDARD', :'htg'),
 ('22222222-0000-0000-0000-00000000000b', '11111111-0000-0000-0000-000000000001', 'Supermarché ABC', 'supermarket', 'STANDARD', :'htg');

INSERT INTO establishment (id, company_id, name, code)
VALUES ('33333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'Siège', 'MAIN');
INSERT INTO store (id, establishment_id, company_id, name, code)
VALUES ('34333333-0000-0000-0000-000000000001', '33333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'Boutique', 'S1');
INSERT INTO inventory_location (id, company_id, establishment_id, store_id, name, location_type)
VALUES ('35333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '33333333-0000-0000-0000-000000000001',
        '34333333-0000-0000-0000-000000000001', 'Rayon', 'store');

INSERT INTO unit (id, company_id, name, code)      VALUES ('36333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'Unité', 'U');
INSERT INTO tax  (id, company_id, name, code, rate) VALUES ('37333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'TCA 10%', 'TCA10', 10);

INSERT INTO product (id, company_id, sku, name, unit_id)
VALUES ('38333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'SKU-1', 'Paracétamol 500mg', '36333333-0000-0000-0000-000000000001');

INSERT INTO customer (id, company_id, customer_number, name, credit_limit, default_currency_id)
VALUES ('39333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'C-001', 'Client Crédit', 100000, :'htg');
INSERT INTO supplier (id, company_id, supplier_number, name, default_currency_id)
VALUES ('3a333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'F-001', 'Fournisseur A', :'htg');

INSERT INTO financial_account (id, company_id, account_type, name, currency_id)
VALUES ('3b333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'cash', 'Caisse principale', :'htg');

INSERT INTO chart_of_accounts (id, company_id, name, status)
VALUES ('3c333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'PCG ABC', 'active');
INSERT INTO account (id, chart_of_accounts_id, company_id, code, name, account_type, is_postable) VALUES
 ('3c333333-0000-0000-0000-0000000000a1', '3c333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '5711', 'Caisse',         'asset',     true),
 ('3c333333-0000-0000-0000-0000000000a2', '3c333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '4111', 'Clients',        'asset',     true),
 ('3c333333-0000-0000-0000-0000000000a3', '3c333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '7011', 'Ventes',         'revenue',   true),
 ('3c333333-0000-0000-0000-0000000000a4', '3c333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '4457', 'TCA collectée',  'liability', true),
 ('3c333333-0000-0000-0000-0000000000a5', '3c333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '1011', 'Capital',        'equity',    false);

INSERT INTO journal (id, company_id, code, name, journal_type)
VALUES ('3d333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'VT', 'Ventes', 'sales');
INSERT INTO accounting_period (id, company_id, name, start_date, end_date, status) VALUES
 ('3e333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '2026', '2026-01-01', '2026-12-31', 'open'),
 ('3e333333-0000-0000-0000-000000000002', '22222222-0000-0000-0000-00000000000a', '2025', '2025-01-01', '2025-12-31', 'closed');

\echo '--- 1. Réception d''achat : le stock augmente (§32) -------------------'
INSERT INTO goods_receipt (id, company_id, supplier_id, document_number, status, inventory_location_id)
VALUES ('40333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3a333333-0000-0000-0000-000000000001', 'GR-1', 'posted',
        '35333333-0000-0000-0000-000000000001');
INSERT INTO stock_movement (company_id, product_id, inventory_location_id, movement_type, quantity, unit_cost, reference_type, reference_id)
VALUES ('22222222-0000-0000-0000-00000000000a', '38333333-0000-0000-0000-000000000001', '35333333-0000-0000-0000-000000000001',
        'receipt', 100, 40, 'goods_receipt', '40333333-0000-0000-0000-000000000001');

DO $$
DECLARE v numeric;
BEGIN
    SELECT quantity_on_hand INTO v FROM stock_balance WHERE product_id = '38333333-0000-0000-0000-000000000001';
    IF v <> 100 THEN RAISE EXCEPTION 'ASSERT: stock attendu 100, obtenu %', v; END IF;
    RAISE NOTICE 'OK  stock_balance = % après réception', v;
END $$;

\echo '--- 2. Vente au comptant : stock -, encaissement + (§32) ------------'
INSERT INTO sale (id, company_id, store_id, document_number, status, sale_type, currency_id, subtotal, tax_total, total)
VALUES ('41333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '34333333-0000-0000-0000-000000000001',
        'S-1', 'completed', 'cash', :'htg', 1000, 100, 1100);
INSERT INTO sale_line (sale_id, company_id, product_id, quantity, unit_price, tax_amount, line_total, cost_snapshot)
VALUES ('41333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '38333333-0000-0000-0000-000000000001', 10, 100, 100, 1000, 40);
INSERT INTO stock_movement (company_id, product_id, inventory_location_id, movement_type, quantity, unit_cost, reference_type, reference_id)
VALUES ('22222222-0000-0000-0000-00000000000a', '38333333-0000-0000-0000-000000000001', '35333333-0000-0000-0000-000000000001',
        'sale', -10, 40, 'sale', '41333333-0000-0000-0000-000000000001');
INSERT INTO payment (id, company_id, payment_number, direction, financial_account_id, currency_id, amount)
VALUES ('42333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'P-1', 'inbound', '3b333333-0000-0000-0000-000000000001', :'htg', 1100);
INSERT INTO sale_payment (sale_id, company_id, payment_id, amount, currency_id)
VALUES ('41333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '42333333-0000-0000-0000-000000000001', 1100, :'htg');

DO $$
DECLARE v numeric; d numeric;
BEGIN
    SELECT quantity_on_hand INTO v FROM stock_balance WHERE product_id = '38333333-0000-0000-0000-000000000001';
    SELECT amount_paid INTO d FROM sale WHERE id = '41333333-0000-0000-0000-000000000001';
    IF v <> 90   THEN RAISE EXCEPTION 'ASSERT: stock attendu 90, obtenu %', v; END IF;
    IF d <> 1100 THEN RAISE EXCEPTION 'ASSERT: sale.amount_paid attendu 1100, obtenu %', d; END IF;
    RAISE NOTICE 'OK  stock=%, sale.amount_paid=%', v, d;
END $$;

\echo '--- 3. Écriture comptable équilibrée (§20) -------------------------'
INSERT INTO journal_entry (id, company_id, journal_id, accounting_period_id, entry_number, entry_date, status, source_type, source_id)
VALUES ('43333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3d333333-0000-0000-0000-000000000001',
        '3e333333-0000-0000-0000-000000000001', 'VT-1', '2026-03-01', 'draft', 'sale', '41333333-0000-0000-0000-000000000001');
INSERT INTO journal_entry_line (journal_entry_id, company_id, account_id, currency_id, debit, credit, base_debit, base_credit) VALUES
 ('43333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3c333333-0000-0000-0000-0000000000a1', :'htg', 1100, 0, 1100, 0),
 ('43333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3c333333-0000-0000-0000-0000000000a3', :'htg', 0, 1000, 0, 1000),
 ('43333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3c333333-0000-0000-0000-0000000000a4', :'htg', 0, 100, 0, 100);
UPDATE journal_entry SET status = 'posted', posted_at = now() WHERE id = '43333333-0000-0000-0000-000000000001';
SET CONSTRAINTS ALL IMMEDIATE;
\echo 'OK  écriture équilibrée (1100 = 1000 + 100) validée'
SET CONSTRAINTS ALL DEFERRED;

\echo '--- 4. Vente à crédit + règlement partiel (§12, §15, §32) ---------'
INSERT INTO sale (id, company_id, store_id, customer_id, document_number, status, sale_type, currency_id, subtotal, total)
VALUES ('44333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '34333333-0000-0000-0000-000000000001',
        '39333333-0000-0000-0000-000000000001', 'S-2', 'completed', 'credit', :'htg', 5000, 5000);
INSERT INTO customer_receivable (id, company_id, sale_id, customer_id, currency_id, original_amount, due_date)
VALUES ('45333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '44333333-0000-0000-0000-000000000001',
        '39333333-0000-0000-0000-000000000001', :'htg', 5000, current_date + 30);
INSERT INTO payment (id, company_id, payment_number, direction, financial_account_id, currency_id, amount)
VALUES ('46333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'P-2', 'inbound', '3b333333-0000-0000-0000-000000000001', :'htg', 2000);
INSERT INTO payment_allocation (company_id, payment_id, receivable_id, allocated_amount, currency_id)
VALUES ('22222222-0000-0000-0000-00000000000a', '46333333-0000-0000-0000-000000000001', '45333333-0000-0000-0000-000000000001', 2000, :'htg');

DO $$
DECLARE r record;
BEGIN
    SELECT paid_amount, remaining_amount, status INTO r FROM customer_receivable WHERE id = '45333333-0000-0000-0000-000000000001';
    IF r.paid_amount <> 2000 OR r.remaining_amount <> 3000 OR r.status <> 'PARTIALLY_PAID' THEN
        RAISE EXCEPTION 'ASSERT: créance payé=% restant=% statut=%', r.paid_amount, r.remaining_amount, r.status;
    END IF;
    RAISE NOTICE 'OK  créance 5000 -> payé 2000 -> restant 3000 (PARTIALLY_PAID)';
END $$;

\echo '--- 5. Achat à crédit + dette + règlement total (§15, §32) -------'
INSERT INTO supplier_invoice (id, company_id, supplier_id, document_number, currency_id, subtotal, total, status, due_date)
VALUES ('47333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3a333333-0000-0000-0000-000000000001',
        'SI-1', :'htg', 4000, 4000, 'validated', current_date + 60);
INSERT INTO supplier_payable (id, company_id, supplier_invoice_id, supplier_id, currency_id, original_amount, due_date)
VALUES ('48333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '47333333-0000-0000-0000-000000000001',
        '3a333333-0000-0000-0000-000000000001', :'htg', 4000, current_date + 60);
INSERT INTO payment (id, company_id, payment_number, direction, financial_account_id, currency_id, amount)
VALUES ('49333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', 'P-3', 'outbound', '3b333333-0000-0000-0000-000000000001', :'htg', 4000);
INSERT INTO payment_allocation (company_id, payment_id, payable_id, allocated_amount, currency_id)
VALUES ('22222222-0000-0000-0000-00000000000a', '49333333-0000-0000-0000-000000000001', '48333333-0000-0000-0000-000000000001', 4000, :'htg');

DO $$
DECLARE r record;
BEGIN
    SELECT remaining_amount, status INTO r FROM supplier_payable WHERE id = '48333333-0000-0000-0000-000000000001';
    IF r.remaining_amount <> 0 OR r.status <> 'PAID' THEN
        RAISE EXCEPTION 'ASSERT: dette restant=% statut=%', r.remaining_amount, r.status;
    END IF;
    RAISE NOTICE 'OK  dette 4000 -> soldée (PAID)';
END $$;

\echo '--- 6. CONTRÔLES : rejets attendus -------------------------------'

-- 6a. Écriture déséquilibrée
SAVEPOINT s6a;
INSERT INTO journal_entry (id, company_id, journal_id, accounting_period_id, entry_number, entry_date, status)
VALUES ('4b333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3d333333-0000-0000-0000-000000000001',
        '3e333333-0000-0000-0000-000000000001', 'VT-Y', '2026-03-03', 'draft');
INSERT INTO journal_entry_line (journal_entry_id, company_id, account_id, currency_id, debit, credit, base_debit, base_credit) VALUES
 ('4b333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3c333333-0000-0000-0000-0000000000a1', :'htg', 100, 0, 100, 0),
 ('4b333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3c333333-0000-0000-0000-0000000000a3', :'htg', 0,  90, 0,  90);
UPDATE journal_entry SET status = 'posted' WHERE id = '4b333333-0000-0000-0000-000000000001';
\set VERBOSITY terse
DO $$
BEGIN
    SET CONSTRAINTS ALL IMMEDIATE;
    RAISE EXCEPTION 'ASSERT FAILED: écriture déséquilibrée acceptée';
EXCEPTION
    WHEN check_violation THEN RAISE NOTICE 'OK  écriture déséquilibrée (débit 100 <> crédit 90) rejetée';
END $$;
ROLLBACK TO SAVEPOINT s6a;
SET CONSTRAINTS ALL DEFERRED;

-- 6b. Compte non postable
SAVEPOINT s6b;
DO $$
BEGIN
    INSERT INTO journal_entry (id, company_id, journal_id, accounting_period_id, entry_number, entry_date, status)
    VALUES ('4c333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3d333333-0000-0000-0000-000000000001',
            '3e333333-0000-0000-0000-000000000001', 'VT-Z', '2026-03-04', 'draft');
    INSERT INTO journal_entry_line (journal_entry_id, company_id, account_id, currency_id, debit, credit, base_debit, base_credit)
    VALUES ('4c333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3c333333-0000-0000-0000-0000000000a5',
            (SELECT id FROM nexora.currency WHERE code = 'HTG'), 100, 0, 100, 0);
    RAISE EXCEPTION 'ASSERT FAILED: ligne sur compte non postable acceptée';
EXCEPTION
    WHEN check_violation THEN RAISE NOTICE 'OK  ligne sur compte non postable (1011 Capital) rejetée';
END $$;
ROLLBACK TO SAVEPOINT s6b;

-- 6c. Période clôturée
SAVEPOINT s6c;
DO $$
BEGIN
    INSERT INTO journal_entry (id, company_id, journal_id, accounting_period_id, entry_number, entry_date, status)
    VALUES ('4d333333-0000-0000-0000-000000000001', '22222222-0000-0000-0000-00000000000a', '3d333333-0000-0000-0000-000000000001',
            '3e333333-0000-0000-0000-000000000002', 'CL-1', '2025-06-01', 'draft');
    RAISE EXCEPTION 'ASSERT FAILED: écriture dans période clôturée acceptée';
EXCEPTION
    WHEN check_violation THEN RAISE NOTICE 'OK  écriture dans période clôturée (2025) rejetée';
END $$;
ROLLBACK TO SAVEPOINT s6c;

-- 6d. Sur-affectation d'un paiement
SAVEPOINT s6d;
DO $$
BEGIN
    INSERT INTO payment_allocation (company_id, payment_id, receivable_id, allocated_amount, currency_id)
    VALUES ('22222222-0000-0000-0000-00000000000a', '46333333-0000-0000-0000-000000000001',
            '45333333-0000-0000-0000-000000000001', 999999,
            (SELECT id FROM nexora.currency WHERE code = 'HTG'));
    RAISE EXCEPTION 'ASSERT FAILED: sur-affectation de paiement acceptée';
EXCEPTION
    WHEN check_violation THEN RAISE NOTICE 'OK  affectation (999999) > montant du paiement (2000) rejetée';
END $$;
ROLLBACK TO SAVEPOINT s6d;

-- 6e. Suppression physique d'un paiement validé
SAVEPOINT s6e;
DO $$
BEGIN
    DELETE FROM payment WHERE id = '42333333-0000-0000-0000-000000000001';
    RAISE EXCEPTION 'ASSERT FAILED: suppression d''un paiement validé acceptée';
EXCEPTION
    WHEN check_violation THEN RAISE NOTICE 'OK  suppression physique d''un paiement validé rejetée';
END $$;
ROLLBACK TO SAVEPOINT s6e;
\set VERBOSITY default

\echo '--- 7. Isolation multi-tenant (RLS §34) --------------------------'
SET LOCAL nexora.bypass_rls = 'off';

SET LOCAL nexora.company_ids = '22222222-0000-0000-0000-00000000000b';   -- Supermarché uniquement
SET LOCAL ROLE nexora_app;
DO $$
DECLARE n integer;
BEGIN
    SELECT count(*) INTO n FROM sale;
    IF n <> 0 THEN RAISE EXCEPTION 'ASSERT: RLS a laissé passer % ventes hors périmètre', n; END IF;
    RAISE NOTICE 'OK  périmètre Supermarché : 0 vente visible (les ventes sont à la Pharmacie)';
END $$;
RESET ROLE;

SET LOCAL nexora.company_ids = '22222222-0000-0000-0000-00000000000a';   -- Pharmacie
SET LOCAL ROLE nexora_app;
DO $$
DECLARE n integer;
BEGIN
    SELECT count(*) INTO n FROM sale;
    IF n <> 2 THEN RAISE EXCEPTION 'ASSERT: périmètre Pharmacie devrait voir 2 ventes, vu %', n; END IF;
    RAISE NOTICE 'OK  périmètre Pharmacie : 2 ventes visibles';
END $$;
RESET ROLE;

\echo ''
\echo '============================================================'
\echo ' TOUS LES CONTROLES DU MODELE DE DONNEES SONT PASSES'
\echo '============================================================'

ROLLBACK;
