-- =============================================================================
-- NEXORA ONE — Jeu de recette (Phase 55) — 1/2 : rendre le Supermarché ABC opérationnel
--
-- La démo ABC GROUP ne configure que la Pharmacie. Pour tester l'isolation multi-entreprise, la
-- consolidation et les scénarios « commerce » (PLU, rayons, promotions, produits pesés), une
-- seconde entreprise doit pouvoir vendre, acheter et comptabiliser. Ce fichier pose UNIQUEMENT le
-- référentiel sans API (établissement, emplacements, comptes, correspondances comptables, caisses,
-- séries documentaires) — calqué sur la Pharmacie. Tout le reste (produits, tiers, utilisateurs,
-- transactions) est créé par le générateur `backend/tools/Nexora.RecetteSeed`, via les services
-- métier, jamais en SQL.
--
-- À charger APRÈS demo_abc_group.sql, dans une base de recette — JAMAIS dans `nexora`, qui sert
-- de modèle aux tests d'intégration.
-- =============================================================================
SET search_path TO nexora, public;
\set super '''dce00000-0000-0000-0000-0000000000c3'''

-- ---- Établissement, magasin, caisse, emplacements --------------------------------------------
INSERT INTO nexora.establishment (id, company_id, name, code)
VALUES ('dce00000-0000-0000-0003-000000000001', :super, 'Supermarché ABC — Pétion-Ville', 'PV');
INSERT INTO nexora.store (id, establishment_id, company_id, name, code)
VALUES ('dce00000-0000-0000-0003-000000000002', 'dce00000-0000-0000-0003-000000000001', :super, 'Surface de vente', 'S1');
INSERT INTO nexora.pos_terminal (id, store_id, company_id, name, code) VALUES
 ('dce00000-0000-0000-0003-000000000003', 'dce00000-0000-0000-0003-000000000002', :super, 'Caisse 1', 'T1'),
 ('dce00000-0000-0000-0003-000000000004', 'dce00000-0000-0000-0003-000000000002', :super, 'Caisse 2', 'T2');
INSERT INTO nexora.warehouse (id, establishment_id, company_id, name, code, type)
VALUES ('dce00000-0000-0000-0003-000000000005', 'dce00000-0000-0000-0003-000000000001', :super, 'Réserve', 'W1', 'general');
INSERT INTO nexora.inventory_location (id, company_id, establishment_id, store_id, warehouse_id, name, location_type) VALUES
 ('dce00000-0000-0000-0003-000000000006', :super, 'dce00000-0000-0000-0003-000000000001',
   'dce00000-0000-0000-0003-000000000002', NULL, 'Rayons', 'store'),
 ('dce00000-0000-0000-0003-000000000007', :super, 'dce00000-0000-0000-0003-000000000001',
   NULL, 'dce00000-0000-0000-0003-000000000005', 'Réserve', 'warehouse');

-- ---- Unités & taxes -------------------------------------------------------------------------
INSERT INTO nexora.unit (id, company_id, name, code) VALUES
 ('dce00000-0000-0000-0003-000000000011', :super, 'Pièce', 'PCE'),
 ('dce00000-0000-0000-0003-000000000012', :super, 'Kilogramme', 'KG'),
 ('dce00000-0000-0000-0003-000000000013', :super, 'Paquet', 'PAQ');
INSERT INTO nexora.tax (id, company_id, name, code, rate, type, is_inclusive) VALUES
 ('dce00000-0000-0000-0003-000000000021', :super, 'Exonéré', 'EX0', 0, 'none', false),
 ('dce00000-0000-0000-0003-000000000022', :super, 'TVA 10 %', 'TVA10', 10, 'vat', false);

-- ---- Comptes financiers, caisse enregistreuse, moyens de paiement ---------------------------
INSERT INTO nexora.financial_account (id, company_id, account_type, name, currency_id, opening_balance, current_balance) VALUES
 ('dce00000-0000-0000-0003-000000000051', :super, 'cash', 'Caisse magasin', (SELECT id FROM nexora.currency WHERE code='HTG'), 60000, 60000),
 ('dce00000-0000-0000-0003-000000000052', :super, 'bank', 'Compte BNC', (SELECT id FROM nexora.currency WHERE code='HTG'), 850000, 850000),
 ('dce00000-0000-0000-0003-000000000053', :super, 'mobile_money', 'MonCash marchand', (SELECT id FROM nexora.currency WHERE code='HTG'), 15000, 15000);
INSERT INTO nexora.cash_register (id, company_id, store_id, financial_account_id, name, code, currency_id)
VALUES ('dce00000-0000-0000-0003-000000000061', :super, 'dce00000-0000-0000-0003-000000000002',
        'dce00000-0000-0000-0003-000000000051', 'Caisse 1', 'R1', (SELECT id FROM nexora.currency WHERE code='HTG'));
INSERT INTO nexora.payment_method (id, company_id, name, type, currency_id, requires_reference) VALUES
 ('dce00000-0000-0000-0003-000000000081', :super, 'Espèces HTG', 'cash', (SELECT id FROM nexora.currency WHERE code='HTG'), false),
 ('dce00000-0000-0000-0003-000000000082', :super, 'Carte', 'card', (SELECT id FROM nexora.currency WHERE code='HTG'), true),
 ('dce00000-0000-0000-0003-000000000083', :super, 'MonCash', 'mobile_money', (SELECT id FROM nexora.currency WHERE code='HTG'), true);

-- ---- Plan comptable & correspondances (identiques à la Pharmacie) ---------------------------
INSERT INTO nexora.chart_of_accounts (id, company_id, name, status)
VALUES ('dce00000-0000-0000-0003-000000000071', :super, 'Plan comptable Supermarché ABC', 'active');
INSERT INTO nexora.account (chart_of_accounts_id, company_id, code, name, account_type)
SELECT 'dce00000-0000-0000-0003-000000000071', :super, a.code, a.name, a.account_type
FROM nexora.account a
WHERE a.company_id = 'dce00000-0000-0000-0000-0000000000c1'
  AND a.chart_of_accounts_id = 'dce00000-0000-0000-0000-00000000a071';

UPDATE nexora.financial_account fa SET ledger_account_id = a.id
FROM nexora.account a
WHERE a.company_id = :super AND a.chart_of_accounts_id = 'dce00000-0000-0000-0003-000000000071'
  AND ((fa.id = 'dce00000-0000-0000-0003-000000000051' AND a.code = '5711')
    OR (fa.id IN ('dce00000-0000-0000-0003-000000000052', 'dce00000-0000-0000-0003-000000000053') AND a.code = '5121'));

INSERT INTO nexora.account_mapping (company_id, mapping_key, account_id)
SELECT :super, m.mapping_key, s.id
FROM nexora.account_mapping m
JOIN nexora.account p ON p.id = m.account_id
JOIN nexora.account s ON s.company_id = :super
     AND s.chart_of_accounts_id = 'dce00000-0000-0000-0003-000000000071' AND s.code = p.code
WHERE m.company_id = 'dce00000-0000-0000-0000-0000000000c1';

INSERT INTO nexora.journal (company_id, code, name, journal_type) VALUES
 (:super, 'VT', 'Journal des ventes',  'sales'),
 (:super, 'AC', 'Journal des achats',  'purchases'),
 (:super, 'CA', 'Journal de caisse',   'cash'),
 (:super, 'BQ', 'Journal de banque',   'bank'),
 (:super, 'OD', 'Opérations diverses', 'general'),
 (:super, 'AN', 'À-nouveaux',          'opening'),
 (:super, 'CL', 'Clôture',             'closing');

INSERT INTO nexora.accounting_period (company_id, name, start_date, end_date, status)
VALUES (:super, 'Exercice 2026', '2026-01-01', '2026-12-31', 'open');

INSERT INTO nexora.document_sequence (company_id, document_type, year, prefix, include_year) VALUES
 (:super, 'sale',                    2026, 'SMV', true),
 (:super, 'supplier_invoice',        2026, 'SMF', true),
 (:super, 'payment',                 2026, 'SMR', true),
 (:super, 'goods_receipt',           2026, 'SMB', true),
 (:super, 'quote',                   2026, 'SMD', true),
 (:super, 'document:invoice',        2026, 'SMI', true),
 (:super, 'document:credit_note',    2026, 'SMA', true),
 (:super, 'document:receipt',        2026, 'SMT', true)
ON CONFLICT ON CONSTRAINT document_sequence_uk DO NOTHING;

-- Liste tarifaire « Grossiste » (clients professionnels)
INSERT INTO nexora.price_list (id, company_id, code, name, list_type, priority)
VALUES ('dce00000-0000-0000-0003-000000000025', :super, 'PRO', 'Revendeurs', 'wholesale', 10);

\echo 'Recette 1/2 : Supermarché ABC opérationnel (emplacements, comptes, caisse, séries).'
