-- =============================================================================
-- NEXORA — V097 — Permissions sans effet (analyse exhaustive 2026-10, §6.2)
--
-- 102 des 316 permissions n'étaient vérifiées nulle part : un administrateur pouvait les retirer d'un rôle en
-- croyant restreindre une action (« ce caissier n'a pas pos.reprint ») sans rien restreindre. Ce sont les restes
-- d'une matrice générique créer / modifier / supprimer / exporter des premières migrations ; les actions réelles
-- sont protégées par d'autres permissions (ex. purchasing.approve_order, reporting.export).
--
-- pos.reprint correspond à une vraie action et est désormais vérifiée : réimprimer un ticket de vente déjà sorti
-- (duplicata). Les 101 autres sont retirées des rôles puis supprimées (le retrait est tracé par l'audit des rôles).
-- Idempotente.
-- =============================================================================
UPDATE nexora.permission
   SET name = 'Caisse — réimprimer un ticket de vente',
       description = 'Sortir une nouvelle copie (duplicata) d''un ticket de vente déjà imprimé.'
 WHERE code = 'pos.reprint';

-- La suppression d'une permission retire aussi ses attributions aux rôles (clé en cascade, tracée par l'audit).
DELETE FROM nexora.permission p
 USING (VALUES
    ('accounting.delete'), ('accounting.reconcile'), ('accounting.update'), ('admin.create'), ('admin.delete'),
    ('admin.export'), ('admin.manage_devices'), ('admin.update'), ('admin.view'), ('analytics.create'),
    ('analytics.delete'), ('analytics.export'), ('analytics.update'), ('analytics.view'), ('approvals.approve'),
    ('approvals.create'), ('approvals.delete'), ('approvals.export'), ('approvals.update'), ('approvals.view'),
    ('audit.create'), ('audit.delete'), ('audit.export'), ('audit.update'), ('audit.view'),
    ('cash.create'), ('cash.delete'), ('cash.export'), ('cash.update'), ('cash.view'),
    ('catalog.delete'), ('catalog.export'), ('configuration.create'), ('configuration.delete'), ('configuration.export'),
    ('configuration.update'), ('configuration.view'), ('consolidation.create'), ('consolidation.delete'), ('consolidation.export'),
    ('consolidation.update'), ('consolidation.view'), ('crm.create'), ('crm.delete'), ('crm.export'),
    ('crm.update'), ('crm.view'), ('customers.delete'), ('documents.create'), ('documents.delete'),
    ('documents.export'), ('documents.update'), ('finance.delete'), ('identity.create'), ('identity.delete'),
    ('identity.export'), ('identity.update'), ('identity.view'), ('integrations.create'), ('integrations.delete'),
    ('integrations.export'), ('integrations.update'), ('inventory.create'), ('inventory.delete'), ('inventory.export'),
    ('inventory.update'), ('notifications.create'), ('notifications.delete'), ('notifications.export'), ('notifications.update'),
    ('notifications.view'), ('organizations.delete'), ('organizations.export'), ('payments.create'), ('payments.delete'),
    ('payments.export'), ('payments.update'), ('payments.view'), ('pos.create'), ('pos.delete'),
    ('pos.export'), ('pos.update'), ('purchasing.approve'), ('purchasing.delete'), ('purchasing.export'),
    ('purchasing.request'), ('purchasing.update'), ('reporting.create'), ('reporting.delete'), ('reporting.update'),
    ('sales.delete'), ('sales.export'), ('sales.update'), ('suppliers.delete'), ('suppliers.export'),
    ('suppliers.manage'), ('synchronization.create'), ('synchronization.delete'), ('synchronization.export'), ('synchronization.update'),
    ('synchronization.view')
       ) AS unused(code)
 WHERE p.code = unused.code;
