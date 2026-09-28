-- =============================================================================
-- NEXORA — V083 — Acompte de commande client (finalisation produit, phase 4)
--
-- Défauts corrigés :
--  1. L'acompte était encaissé (Dr trésorerie / Cr compte d'attente) mais n'apparaissait nulle part
--     dans le compte du client (aucune entrée au journal client).
--  2. À l'exécution de la commande, la vente ignorait l'acompte : le client payait la totalité
--     une seconde fois, et l'acompte restait indéfiniment en compte d'attente.
--
-- L'acompte devient un avoir client (module clients, V073) d'origine « order_deposit », rattaché à la
-- commande : il apparaît au compte du client, il est imputé sur la vente à l'exécution (même mécanisme
-- que le paiement par avoir), et il reste disponible (ou remboursable) si la commande est annulée.
-- Idempotente.
-- =============================================================================
ALTER TABLE nexora.customer_credit_memo ADD COLUMN IF NOT EXISTS sales_order_id uuid;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customer_credit_memo_order_fk') THEN
        ALTER TABLE nexora.customer_credit_memo ADD CONSTRAINT customer_credit_memo_order_fk
            FOREIGN KEY (sales_order_id, company_id) REFERENCES nexora.sales_order (id, company_id);
    END IF;

    ALTER TABLE nexora.customer_credit_memo DROP CONSTRAINT IF EXISTS customer_credit_memo_source_chk;
    ALTER TABLE nexora.customer_credit_memo ADD CONSTRAINT customer_credit_memo_source_chk
        CHECK (source_type = ANY (ARRAY['sale_return', 'overpayment', 'order_deposit']));

    ALTER TABLE nexora.customer_credit_memo DROP CONSTRAINT IF EXISTS customer_credit_memo_origin_chk;
    ALTER TABLE nexora.customer_credit_memo ADD CONSTRAINT customer_credit_memo_origin_chk
        CHECK ((source_type = 'sale_return' AND sale_return_id IS NOT NULL)
            OR (source_type = 'overpayment' AND payment_id IS NOT NULL)
            OR (source_type = 'order_deposit' AND payment_id IS NOT NULL AND sales_order_id IS NOT NULL));

    ALTER TABLE nexora.customer_ledger_entry DROP CONSTRAINT IF EXISTS customer_ledger_entry_type_chk;
    ALTER TABLE nexora.customer_ledger_entry ADD CONSTRAINT customer_ledger_entry_type_chk
        CHECK (entry_type = ANY (ARRAY['credit_sale', 'customer_expense', 'opening', 'manual_debit', 'payment',
            'payment_void', 'sale_return', 'overpayment', 'order_deposit', 'write_off', 'refund', 'credit_cancelled',
            'credit_memo_cancelled']));
END;
$$;

CREATE INDEX IF NOT EXISTS customer_credit_memo_order_idx ON nexora.customer_credit_memo (sales_order_id) WHERE sales_order_id IS NOT NULL;

-- Libellé du journal client selon l'origine de l'avoir.
CREATE OR REPLACE FUNCTION nexora.trg_ledger_credit_memo()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        PERFORM nexora.customer_ledger_write(NEW.company_id, NEW.customer_id, NEW.source_type, 0, NEW.amount,
            NEW.currency_id, NULL, NEW.payment_id, NEW.id, NEW.source_type,
            coalesce(NEW.sale_return_id, NEW.payment_id), NEW.memo_number,
            CASE NEW.source_type
                WHEN 'sale_return' THEN 'Avoir sur retour'
                WHEN 'order_deposit' THEN 'Acompte sur commande'
                ELSE 'Trop-perçu converti en avoir' END
                || coalesce(' — ' || NEW.reason, ''),
            NEW.establishment_id, NULL);
    ELSIF NEW.status = 'cancelled' AND OLD.status <> 'cancelled' AND NEW.amount - NEW.used_amount > 0 THEN
        PERFORM nexora.customer_ledger_write(NEW.company_id, NEW.customer_id, 'credit_memo_cancelled',
            NEW.amount - NEW.used_amount, 0, NEW.currency_id, NULL, NEW.payment_id, NEW.id, NEW.source_type,
            coalesce(NEW.sale_return_id, NEW.payment_id), NEW.memo_number,
            'Annulation de l''avoir — ' || coalesce(NEW.cancel_reason, ''), NEW.establishment_id, NEW.cancelled_at);
    END IF;
    RETURN NULL;
END;
$$;

-- Reprise : acomptes déjà encaissés (V038 → V082) sans avoir. Chaque paiement d'acompte non affecté
-- d'une commande client devient un avoir « order_deposit » du montant non encore affecté.
INSERT INTO nexora.customer_credit_memo
    (company_id, memo_number, customer_id, source_type, payment_id, sales_order_id, currency_id, amount,
     gl_account_key, reason, created_by, created_at)
SELECT o.company_id, 'AVC-ACPT-' || p.payment_number, o.customer_id, 'order_deposit', p.id, o.id, p.currency_id,
       p.amount - p.allocated_amount, 'suspense', 'Reprise acompte ' || o.document_number, p.created_by, p.created_at
FROM nexora.sales_order o
JOIN nexora.payment p ON p.id = o.deposit_payment_id
WHERE o.customer_id IS NOT NULL
  AND p.amount - p.allocated_amount > 0
  AND p.status <> 'void'
  AND NOT EXISTS (SELECT 1 FROM nexora.customer_credit_memo m WHERE m.payment_id = p.id);

-- Audit des lignes de commande client (la commande et le devis l'étaient déjà).
DROP TRIGGER IF EXISTS trg_audit_sales_order_line ON nexora.sales_order_line;
CREATE TRIGGER trg_audit_sales_order_line AFTER INSERT OR UPDATE OR DELETE ON nexora.sales_order_line
    FOR EACH ROW EXECUTE FUNCTION nexora.audit_row();
