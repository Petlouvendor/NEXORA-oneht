-- =============================================================================
-- NEXORA — V088 — Libellés des permissions en français (finalisation produit, phase 11)
--
-- L'écran des rôles affiche désormais les permissions par module : 125 libellés générés automatiquement étaient
-- restés en anglais (« Inventory — create »), et plusieurs préfixes de module aussi (« Sales — … »).
-- Les CODES ne changent pas (ils sont la référence technique) ; seuls les libellés affichés sont traduits.
-- Idempotente.
-- =============================================================================
WITH modules(en, fr) AS (VALUES
    ('Accounting', 'Comptabilité'), ('Admin', 'Administration'), ('Analytics', 'Analyses'), ('Approvals', 'Approbations'),
    ('Audit', 'Audit'), ('Cash', 'Trésorerie'), ('Catalog', 'Catalogue'), ('Crm', 'Relation client'), ('Customers', 'Clients'),
    ('Documents', 'Documents'), ('Finance', 'Finance'), ('Identity', 'Identité'), ('Integrations', 'Intégrations'),
    ('Inventory', 'Stock'), ('Migration', 'Migration'), ('Notifications', 'Notifications'), ('Offline', 'Hors ligne'),
    ('Organizations', 'Organisations'), ('Payments', 'Paiements'), ('Pos', 'Caisse'), ('POS', 'Caisse'), ('Purchasing', 'Achats'),
    ('Reporting', 'Rapports'), ('Sales', 'Ventes'), ('Suppliers', 'Fournisseurs'), ('Synchronization', 'Synchronisation'),
    ('Intelligence', 'Intelligence')),
verbs(en, fr) AS (VALUES
    ('view', 'consulter'), ('create', 'créer'), ('update', 'modifier'), ('delete', 'supprimer'), ('export', 'exporter'))
UPDATE nexora.permission p
   SET name = COALESCE(m.fr, split_part(p.name, ' — ', 1)) || ' — ' || COALESCE(v.fr, substr(p.name, strpos(p.name, ' — ') + 3))
  FROM nexora.permission x
  LEFT JOIN modules m ON m.en = split_part(x.name, ' — ', 1)
  LEFT JOIN verbs v ON v.en = substr(x.name, strpos(x.name, ' — ') + 3)
 WHERE p.id = x.id
   AND strpos(x.name, ' — ') > 0
   AND (m.en IS NOT NULL OR v.en IS NOT NULL);
