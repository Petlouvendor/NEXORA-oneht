-- =============================================================================
-- NEXORA — V090 — Groupe : opérations intersociétés, correspondances, consolidation, politique
-- (finalisation produit, phase 14)
--
--  * Référence de pièce d'une opération intersociétés (n° de facture, de virement…) : elle était
--    rangée dans la colonne « type de référence », prévue pour un type de document.
--  * Motif d'archivage d'une société : exigé par l'API mais conservé nulle part.
--  * Journal d'audit sur les tables de gouvernance de groupe, qui n'étaient pas tracées : société,
--    plan comptable groupe, correspondances, politique, opérations et rapprochements intersociétés,
--    consolidations, tâches.
-- Idempotente.
-- =============================================================================
ALTER TABLE nexora.intercompany_transaction
    ADD COLUMN IF NOT EXISTS source_document text,
    ADD COLUMN IF NOT EXISTS target_document text,
    ADD COLUMN IF NOT EXISTS created_by uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS confirmed_by uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS confirmed_at timestamptz;

-- Reprise : une référence saisie avant V090 avait atterri dans *_reference_type.
UPDATE nexora.intercompany_transaction
SET source_document = COALESCE(source_document, source_reference_type), source_reference_type = NULL
WHERE source_reference_type IS NOT NULL AND source_reference_id IS NULL;
UPDATE nexora.intercompany_transaction
SET target_document = COALESCE(target_document, target_reference_type), target_reference_type = NULL
WHERE target_reference_type IS NOT NULL AND target_reference_id IS NULL;

ALTER TABLE nexora.company
    ADD COLUMN IF NOT EXISTS archive_reason text;

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['company', 'group_account', 'account_group_mapping', 'group_policy',
                             'intercompany_transaction', 'intercompany_match', 'consolidation_run', 'group_task'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;
