-- =============================================================================
-- NEXORA ONE — Jeu de recette (Phase 55) — contrôle du jeu généré
--
-- Lecture seule. À exécuter sur la base de recette après database/scripts/recette.sh :
--   psql -d nexora_recette -f database/seeds/recette/verif_jeu_recette.sql
-- Chaque ligne correspond à un scénario de docs/phase-55-jeu-de-donnees-recette.md ; une valeur
-- à 0 là où le document attend un signal indique un jeu incomplet (ou une régression).
-- =============================================================================
SET search_path TO nexora, public;
\pset footer off

\echo '== Volumétrie par entreprise'
SELECT c.name AS entreprise,
       (SELECT COUNT(*) FROM product p WHERE p.company_id = c.id)                               AS produits,
       (SELECT COUNT(*) FROM customer x WHERE x.company_id = c.id)                              AS clients,
       (SELECT COUNT(*) FROM supplier x WHERE x.company_id = c.id)                              AS fournisseurs,
       (SELECT COUNT(*) FROM sale s WHERE s.company_id = c.id AND s.status = 'completed')       AS ventes,
       (SELECT COUNT(DISTINCT date_trunc('month', s.sold_at)) FROM sale s WHERE s.company_id = c.id) AS mois_de_ventes,
       (SELECT COUNT(*) FROM goods_receipt g WHERE g.company_id = c.id)                         AS receptions,
       (SELECT COUNT(*) FROM supplier_invoice i WHERE i.company_id = c.id)                      AS factures_fourn,
       (SELECT COUNT(*) FROM journal_entry j WHERE j.company_id = c.id)                         AS ecritures
FROM company c WHERE c.id IN ('dce00000-0000-0000-0000-0000000000c1', 'dce00000-0000-0000-0000-0000000000c3')
ORDER BY 1;

\echo '== Crédit clients : créances ouvertes, échues, encours vs limite'
SELECT co.name AS entreprise, cu.name AS client, cu.credit_limit AS limite,
       COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.status <> 'paid'), 0) AS encours,
       COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.status <> 'paid' AND r.due_date < current_date), 0) AS echu,
       EXISTS (SELECT 1 FROM customer_block b WHERE b.customer_id = cu.id AND b.is_active) AS bloque
FROM customer cu JOIN company co ON co.id = cu.company_id
LEFT JOIN customer_receivable r ON r.customer_id = cu.id
WHERE co.id IN ('dce00000-0000-0000-0000-0000000000c1', 'dce00000-0000-0000-0000-0000000000c3')
GROUP BY co.name, cu.id, cu.name, cu.credit_limit
ORDER BY 1, 2;

\echo '== Fournisseurs : dettes ouvertes / échues, factures en écart de rapprochement'
SELECT co.name AS entreprise, su.name AS fournisseur,
       COALESCE(SUM(p.remaining_amount) FILTER (WHERE p.status <> 'paid'), 0) AS a_payer,
       COALESCE(SUM(p.remaining_amount) FILTER (WHERE p.status <> 'paid' AND p.due_date < current_date), 0) AS echu,
       (SELECT COUNT(*) FROM supplier_invoice i WHERE i.supplier_id = su.id AND i.match_status = 'variance') AS factures_en_ecart
FROM supplier su JOIN company co ON co.id = su.company_id
LEFT JOIN supplier_payable p ON p.supplier_id = su.id
WHERE co.id IN ('dce00000-0000-0000-0000-0000000000c1', 'dce00000-0000-0000-0000-0000000000c3')
GROUP BY co.name, su.id, su.name ORDER BY 1, 2;

\echo '== Stock : sous le point de commande, sans mouvement de vente depuis 60 j, lots à risque'
SELECT co.name AS entreprise, p.sku, sb.quantity_on_hand AS stock, rr.reorder_point AS point_cmd,
       CASE WHEN rr.reorder_point IS NOT NULL AND sb.quantity_on_hand <= rr.reorder_point THEN 'SOUS LE POINT DE COMMANDE'
            WHEN NOT EXISTS (SELECT 1 FROM sale_line sl JOIN sale s ON s.id = sl.sale_id
                             WHERE sl.product_id = p.id AND s.sold_at > now() - interval '60 days') THEN 'DORMANT (60 j)'
       END AS signal
FROM stock_balance sb JOIN product p ON p.id = sb.product_id JOIN company co ON co.id = sb.company_id
LEFT JOIN product_reorder_rule rr ON rr.product_id = p.id AND rr.inventory_location_id = sb.inventory_location_id
WHERE co.id IN ('dce00000-0000-0000-0000-0000000000c1', 'dce00000-0000-0000-0000-0000000000c3')
  AND ((rr.reorder_point IS NOT NULL AND sb.quantity_on_hand <= rr.reorder_point)
       OR NOT EXISTS (SELECT 1 FROM sale_line sl JOIN sale s ON s.id = sl.sale_id
                      WHERE sl.product_id = p.id AND s.sold_at > now() - interval '60 days'))
ORDER BY 1, 4 NULLS LAST, 2;

SELECT p.sku, l.lot_number, l.expiration_date, ls.quantity AS en_stock,
       CASE WHEN l.expiration_date < current_date THEN 'EXPIRÉ' ELSE 'EXPIRE SOUS 30 J' END AS signal
FROM lot l JOIN product p ON p.id = l.product_id
JOIN (SELECT lot_id, SUM(quantity_on_hand) AS quantity FROM lot_stock GROUP BY lot_id) ls ON ls.lot_id = l.id
WHERE l.expiration_date < current_date + 30 AND ls.quantity > 0
ORDER BY l.expiration_date;

\echo '== Caisse, retours, paniers en attente'
SELECT co.name AS entreprise, cs.status, cs.opening_balance, cs.expected_balance, cs.counted_balance, cs.difference
FROM cash_session cs JOIN company co ON co.id = cs.company_id ORDER BY 1;
SELECT co.name AS entreprise, s.status, COUNT(*) FROM sale s JOIN company co ON co.id = s.company_id
WHERE s.status <> 'completed' GROUP BY 1, 2 ORDER BY 1, 2;
SELECT co.name AS entreprise, COUNT(*) AS retours, SUM(r.refund_total) AS rembourse
FROM sale_return r JOIN company co ON co.id = r.company_id GROUP BY 1 ORDER BY 1;

\echo '== Promotions, devis, commandes clients'
SELECT name, promo_type, value, starts_at::date, ends_at::date, status FROM promotion ORDER BY starts_at;
SELECT co.name AS entreprise, q.status, COUNT(*) FROM quote q JOIN company co ON co.id = q.company_id GROUP BY 1, 2;
SELECT co.name AS entreprise, o.status, COUNT(*) FROM sales_order o JOIN company co ON co.id = o.company_id GROUP BY 1, 2;

\echo '== Comptes de recette (mot de passe commun Recette2026!)'
SELECT u.email, u.full_name, u.status FROM app_user u WHERE u.email LIKE '%@recette.demo' ORDER BY u.email;

\echo '== Intelligence : recommandations, anomalies, prévisions, scores'
SELECT co.name AS entreprise, r.rule_key, r.priority_level, COUNT(*)
FROM recommendation r JOIN company co ON co.id = r.company_id GROUP BY 1, 2, 3 ORDER BY 1, 2;
SELECT co.name AS entreprise, i.category, i.severity, i.title FROM insight i JOIN company co ON co.id = i.company_id ORDER BY 1;
SELECT co.name AS entreprise, f.metric, f.horizon_days FROM forecast f JOIN company co ON co.id = f.company_id ORDER BY 1, 2, 3;
SELECT co.name AS entreprise, e.entity_type, COUNT(*) FROM entity_score e JOIN company co ON co.id = e.company_id GROUP BY 1, 2 ORDER BY 1, 2;
