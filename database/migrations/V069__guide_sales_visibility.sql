-- =============================================================================
-- NEXORA — V069 — Guide fonctionnel développeur, étape 7 : visibilité du chiffre d'affaires
-- (docs/guide-fonctionnel-conformite.md, écarts GF-19 à GF-22).
--
-- Guide §18 : le chiffre d'affaires et les ventes de l'entreprise relèvent de la Direction, du
-- Manager et du Comptable — pas du caissier, ni des responsables stock / achats.
--  • reporting.view_sales : CA, nombre de ventes, panier moyen, journal des ventes, top produits.
--  • documents.view_all   : voir TOUS les documents émis (factures, reçus, devis) ; sans elle,
--    seulement ceux qu'on a émis soi-même (réimpression de ses tickets).
-- =============================================================================
SET search_path TO nexora, public;

INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('reporting.view_sales', 'Pilotage — voir le chiffre d''affaires et les ventes', 'reporting', 'view_sales'),
    ('documents.view_all',   'Documents — voir tous les documents émis de l''entreprise', 'documents', 'view_all')
ON CONFLICT (code) DO NOTHING;

-- Le chiffre d'affaires suit la visibilité financière existante (rôles système ET rôles personnalisés).
INSERT INTO nexora.role_permission (role_id, permission_id)
SELECT DISTINCT rp.role_id, sales.id
FROM nexora.role_permission rp
JOIN nexora.permission fin ON fin.id = rp.permission_id AND fin.code = 'reporting.view_financial'
CROSS JOIN nexora.permission sales
WHERE sales.code = 'reporting.view_sales'
ON CONFLICT DO NOTHING;

-- Tous les rôles qui voyaient les documents gardent la vue complète, sauf les postes de vente.
INSERT INTO nexora.role_permission (role_id, permission_id)
SELECT DISTINCT rp.role_id, va.id
FROM nexora.role_permission rp
JOIN nexora.permission dv ON dv.id = rp.permission_id AND dv.code = 'documents.view'
JOIN nexora.role r ON r.id = rp.role_id
CROSS JOIN nexora.permission va
WHERE va.code = 'documents.view_all'
  AND NOT (r.system_role AND r.company_id IS NULL AND r.name IN ('cashier', 'senior_cashier', 'salesperson'))
ON CONFLICT DO NOTHING;

-- Rôles plateforme / administrateurs : tout.
INSERT INTO nexora.role_permission (role_id, permission_id)
SELECT r.id, p.id
FROM nexora.role r
JOIN nexora.permission p ON p.code IN ('reporting.view_sales', 'documents.view_all')
WHERE r.name IN ('super_administrator', 'group_administrator', 'owner_direction')
  AND r.company_id IS NULL AND r.system_role
ON CONFLICT DO NOTHING;
