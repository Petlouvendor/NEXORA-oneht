-- =============================================================================
-- NEXORA — V021 — Données de référence initiales
-- Devises, catalogue de permissions, rôles système et leurs permissions.
-- Idempotent (ON CONFLICT DO NOTHING).
-- =============================================================================
SET search_path TO nexora, public;

-- --- Devises (§18 CDC) ------------------------------------------
INSERT INTO nexora.currency (code, name, symbol, decimal_places) VALUES
    ('HTG', 'Gourde haïtienne',    'G',   2),
    ('USD', 'Dollar américain',    '$',   2),
    ('EUR', 'Euro',                '€',   2),
    ('CAD', 'Dollar canadien',     'CA$', 2)
ON CONFLICT (code) DO NOTHING;

-- --- Catalogue de permissions (module.action) ------------------
INSERT INTO nexora.permission (code, name, module, action)
SELECT m || '.' || a,
       initcap(replace(m,'_',' ')) || ' — ' || a,
       m, a
FROM (VALUES
    ('identity'), ('organizations'), ('configuration'), ('catalog'),
    ('customers'), ('suppliers'), ('inventory'), ('purchasing'),
    ('sales'), ('pos'), ('payments'), ('cash'), ('finance'), ('accounting'),
    ('crm'), ('reporting'), ('notifications'), ('audit'), ('documents'),
    ('synchronization'), ('integrations'), ('analytics'),
    ('admin'), ('consolidation'), ('approvals')
) AS mods(m)
CROSS JOIN (VALUES
    ('view'), ('create'), ('update'), ('delete'), ('export')
) AS acts(a)
ON CONFLICT (code) DO NOTHING;

INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('pos.discount',            'POS — appliquer une remise',            'pos',           'discount'),
    ('pos.refund',              'POS — rembourser',                      'pos',           'refund'),
    ('pos.void',                'POS — annuler une vente',               'pos',           'void'),
    ('pos.close_session',       'POS — clôturer la caisse',              'pos',           'close_session'),
    ('sales.credit',            'Ventes — vente à crédit',               'sales',         'credit'),
    ('purchasing.receive',      'Achats — réceptionner',                 'purchasing',    'receive'),
    ('inventory.adjust',        'Stock — ajuster',                       'inventory',     'adjust'),
    ('inventory.transfer',      'Stock — transférer',                    'inventory',     'transfer'),
    ('finance.reconcile',       'Finance — rapprochement bancaire',      'finance',       'reconcile'),
    ('finance.transfer',        'Finance — transfert entre comptes',     'finance',       'transfer'),
    ('accounting.post',         'Comptabilité — valider une écriture',   'accounting',    'post'),
    ('accounting.close_period', 'Comptabilité — clôturer une période',   'accounting',    'close_period'),
    ('accounting.reopen_period','Comptabilité — rouvrir une période',    'accounting',    'reopen_period'),
    ('consolidation.run',       'Consolidation — produire les états',    'consolidation', 'run'),
    ('approvals.approve',       'Approbations — approuver',              'approvals',     'approve'),
    ('admin.manage_users',      'Admin — gérer les utilisateurs',        'admin',         'manage_users'),
    ('admin.manage_roles',      'Admin — gérer les rôles',               'admin',         'manage_roles'),
    ('admin.configure_company', 'Admin — configurer l''entreprise',      'admin',         'configure_company'),
    ('admin.manage_devices',    'Admin — gérer les appareils',           'admin',         'manage_devices')
ON CONFLICT (code) DO NOTHING;

-- --- Rôles système (§8 CDC) — globaux (company_id NULL) --------
INSERT INTO nexora.role (name, description, system_role) VALUES
    ('super_administrator', 'Super administrateur — accès plateforme complet',            true),
    ('group_administrator', 'Administrateur groupe — toutes les entreprises du groupe',    true),
    ('owner_direction',     'Propriétaire / Direction',                                    true),
    ('manager',             'Manager opérationnel',                                        true),
    ('accountant',          'Comptable',                                                   true),
    ('senior_accountant',   'Comptable senior',                                            true),
    ('chief_accountant',    'Comptable en chef — consolidation',                           true),
    ('cashier',             'Caissier',                                                    true),
    ('stock_manager',       'Gestionnaire de stock',                                       true),
    ('purchasing_manager',  'Responsable achats',                                          true),
    ('salesperson',         'Vendeur',                                                     true),
    ('auditor',             'Auditeur — lecture seule',                                    true)
ON CONFLICT DO NOTHING;

-- --- Attribution des permissions aux rôles système -----------
-- Helper local : associe à un rôle toutes les permissions dont le code correspond à un motif.
CREATE OR REPLACE FUNCTION pg_temp.grant_perms(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id
    FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms('super_administrator', ARRAY['%']);
SELECT pg_temp.grant_perms('group_administrator', ARRAY['%']);
SELECT pg_temp.grant_perms('owner_direction',     ARRAY['%']);

SELECT pg_temp.grant_perms('manager', ARRAY[
    '%.view', '%.export',
    'pos.%', 'sales.%', 'purchasing.%', 'inventory.%',
    'customers.%', 'suppliers.%', 'crm.%', 'approvals.approve',
    'finance.view', 'reporting.%']);

SELECT pg_temp.grant_perms('accountant', ARRAY[
    'accounting.view','accounting.create','accounting.update','accounting.post','accounting.export',
    'finance.view','finance.create','finance.update','finance.reconcile','finance.transfer',
    'reporting.view','reporting.export','customers.view','suppliers.view']);

SELECT pg_temp.grant_perms('senior_accountant', ARRAY[
    'accounting.%','finance.%','reporting.%','customers.view','suppliers.view','approvals.approve']);

SELECT pg_temp.grant_perms('chief_accountant', ARRAY[
    'accounting.%','finance.%','reporting.%','consolidation.%','audit.view',
    'customers.view','suppliers.view','approvals.approve']);

SELECT pg_temp.grant_perms('cashier', ARRAY[
    'pos.%','sales.view','sales.create','customers.view','customers.create']);

SELECT pg_temp.grant_perms('stock_manager', ARRAY[
    'inventory.%','purchasing.view','purchasing.receive','reporting.view','suppliers.view']);

SELECT pg_temp.grant_perms('purchasing_manager', ARRAY[
    'purchasing.%','suppliers.%','inventory.view','inventory.transfer','reporting.view','approvals.approve']);

SELECT pg_temp.grant_perms('salesperson', ARRAY[
    'pos.view','sales.view','sales.create','customers.view','customers.create','crm.view']);

SELECT pg_temp.grant_perms('auditor', ARRAY[
    '%.view','%.export','audit.view']);
