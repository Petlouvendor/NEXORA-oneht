-- =============================================================================
-- NEXORA — V084 — Trésorerie et dépenses (finalisation produit, phase 7)
--
-- 1. Transfert entre comptes : chaque transfert échouait. Ses deux mouvements (sortie, entrée) portent le même
--    identifiant d'opération, or l'unicité portait sur l'identifiant seul. L'unicité devient
--    (identifiant d'opération, compte) : un rejeu reste refusé, les deux jambes sont permises.
-- 2. Dépenses : la table existait sans aucun usage. Catégorie liée à un compte de charge du plan comptable,
--    écriture comptable et motif de refus/annulation conservés, traçabilité soumission → approbation → paiement.
-- 3. Audit des comptes financiers, catégories de dépense et relevés bancaires.
-- Idempotente.
-- =============================================================================
DROP INDEX IF EXISTS nexora.financial_transaction_source_op_uk;
CREATE UNIQUE INDEX IF NOT EXISTS financial_transaction_source_op_account_uk
    ON nexora.financial_transaction (source_operation_uuid, financial_account_id)
    WHERE source_operation_uuid IS NOT NULL;


ALTER TABLE nexora.expense_category ADD COLUMN IF NOT EXISTS ledger_account_id uuid;

ALTER TABLE nexora.expense
    ADD COLUMN IF NOT EXISTS journal_entry_id  uuid,
    ADD COLUMN IF NOT EXISTS submitted_at      timestamptz,
    ADD COLUMN IF NOT EXISTS approved_at       timestamptz,
    ADD COLUMN IF NOT EXISTS paid_by           uuid REFERENCES nexora.app_user (id),
    ADD COLUMN IF NOT EXISTS decision_reason   text,
    ADD COLUMN IF NOT EXISTS reference         text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'expense_category_ledger_fk') THEN
        ALTER TABLE nexora.expense_category ADD CONSTRAINT expense_category_ledger_fk
            FOREIGN KEY (ledger_account_id, company_id) REFERENCES nexora.account (id, company_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'expense_supplier_fk') THEN
        ALTER TABLE nexora.expense ADD CONSTRAINT expense_supplier_fk
            FOREIGN KEY (supplier_id, company_id) REFERENCES nexora.supplier (id, company_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'expense_decision_reason_chk') THEN
        ALTER TABLE nexora.expense ADD CONSTRAINT expense_decision_reason_chk
            CHECK (status NOT IN ('rejected', 'cancelled') OR length(btrim(coalesce(decision_reason, ''))) > 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'expense_paid_chk') THEN
        ALTER TABLE nexora.expense ADD CONSTRAINT expense_paid_chk
            CHECK (status <> 'paid' OR (paid_at IS NOT NULL AND financial_account_id IS NOT NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'expense_category_company_name_uk') THEN
        ALTER TABLE nexora.expense_category ADD CONSTRAINT expense_category_company_name_uk UNIQUE (company_id, name);
    END IF;
END;
$$;

CREATE INDEX IF NOT EXISTS expense_company_status_idx ON nexora.expense (company_id, status, expense_date DESC);

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['financial_account','expense_category','bank_statement','bank_statement_line'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;
