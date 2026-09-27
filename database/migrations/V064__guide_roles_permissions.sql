-- =============================================================================
-- NEXORA — V064 — Guide fonctionnel développeur, étape 1 : rôles & permissions
-- (docs/guide-fonctionnel-conformite.md, écarts GF-01, GF-05, GF-08, GF-14).
--
-- Le caissier est un utilisateur POS, pas un utilisateur de gestion (§7) : on lui retire tout ce
-- qui ouvre les données globales de l'entreprise (pilotage, finance, remboursements, catalogue
-- complet). Les actions protégées (retour, remise exceptionnelle, écart de caisse) passent par une
-- validation Manager portée par des permissions dédiées.
-- =============================================================================
SET search_path TO nexora, public;

-- --- Permissions POS de validation (§8, §10, §12, §13) -----------------------
-- pos.refund existait (V025) sans être branchée : elle devient « valider un retour / rembourser ».
UPDATE nexora.permission SET name = 'POS — valider un retour / rembourser' WHERE code = 'pos.refund';

INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('pos.approve_discount', 'POS — valider une remise exceptionnelle',        'pos',     'approve_discount'),
    ('pos.manage_sessions',  'POS — superviser les sessions de caisse',        'pos',     'manage_sessions'),
    ('finance.create',       'Finance — saisir une opération financière brute', 'finance', 'create')
ON CONFLICT (code) DO NOTHING;

-- --- Limite de remise par rôle (§8, §12) ---------------------------------
-- NULL = pas de limite propre au rôle : le plafond d'entreprise pos.max_discount_percent s'applique.
-- La limite sert deux fois : remise appliquée soi-même (bornée en plus par le plafond d'entreprise)
-- et plafond des remises exceptionnelles que ce rôle peut valider (avec pos.approve_discount).
ALTER TABLE nexora.role ADD COLUMN IF NOT EXISTS max_discount_percent numeric(5,2);
ALTER TABLE nexora.role DROP CONSTRAINT IF EXISTS role_max_discount_chk;
ALTER TABLE nexora.role ADD CONSTRAINT role_max_discount_chk
    CHECK (max_discount_percent IS NULL OR (max_discount_percent >= 0 AND max_discount_percent <= 100));

-- --- Caissier senior (§6, §8) ---------------------------------------------
INSERT INTO nexora.role (name, description, system_role) VALUES
    ('senior_cashier', 'Caissier senior — POS avec limite de remise supérieure', true)
ON CONFLICT DO NOTHING;

UPDATE nexora.role SET max_discount_percent = v.pct
FROM (VALUES ('cashier', 5), ('senior_cashier', 10), ('salesperson', 5), ('manager', 25),
             ('owner_direction', 100), ('group_administrator', 100), ('super_administrator', 100)) AS v(name, pct)
WHERE nexora.role.name = v.name AND nexora.role.company_id IS NULL AND nexora.role.system_role;

-- --- Caissier : périmètre POS strict (§7.2, §27) ------------------------------
-- Retirés : pilotage (reporting.view → Command Center, rapports, BI), finance (dépenses, paiements
-- fournisseurs, comptages), remboursement électronique (integrations.payments), remboursement /
-- annulation (pos.refund, pos.void), catalogue complet avec fiches (catalog.view → remplacé par la
-- recherche POS bornée), actions POS génériques jamais utilisées, tournées terrain.
DELETE FROM nexora.role_permission rp
USING nexora.role r, nexora.permission p
WHERE rp.role_id = r.id AND rp.permission_id = p.id
  AND r.name = 'cashier' AND r.company_id IS NULL AND r.system_role
  AND p.code IN ('reporting.view', 'finance.pay', 'finance.manage_cash', 'integrations.payments',
                 'pos.refund', 'pos.void', 'pos.export', 'pos.delete', 'pos.update',
                 'catalog.view', 'field_ops.view');

-- Caissier senior = caissier (après nettoyage) ; seule la limite de remise diffère par défaut.
-- « Modifier prix » et « modifier produit » restent « selon permission » : non accordés d'office.
INSERT INTO nexora.role_permission (role_id, permission_id)
SELECT senior.id, rp.permission_id
FROM nexora.role senior
JOIN nexora.role cashier ON cashier.name = 'cashier' AND cashier.company_id IS NULL AND cashier.system_role
JOIN nexora.role_permission rp ON rp.role_id = cashier.id
WHERE senior.name = 'senior_cashier' AND senior.company_id IS NULL AND senior.system_role
ON CONFLICT DO NOTHING;

-- --- Validation Manager et administration -------------------------------
INSERT INTO nexora.role_permission (role_id, permission_id)
SELECT r.id, p.id
FROM nexora.role r
JOIN nexora.permission p ON p.code IN ('pos.approve_discount', 'pos.manage_sessions', 'pos.refund')
WHERE r.name IN ('manager', 'owner_direction', 'group_administrator', 'super_administrator')
  AND r.company_id IS NULL AND r.system_role
ON CONFLICT DO NOTHING;

INSERT INTO nexora.role_permission (role_id, permission_id)
SELECT r.id, p.id
FROM nexora.role r
JOIN nexora.permission p ON p.code = 'finance.create'
WHERE r.name IN ('owner_direction', 'group_administrator', 'super_administrator',
                 'accountant', 'senior_accountant', 'chief_accountant')
  AND r.company_id IS NULL AND r.system_role
ON CONFLICT DO NOTHING;
