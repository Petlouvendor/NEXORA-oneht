-- =============================================================================
-- NEXORA — V065 — Guide fonctionnel développeur, étape 2 : retours avec validation Manager
-- (docs/guide-fonctionnel-conformite.md, écarts GF-03, GF-04, GF-13, GF-18).
--
-- CAISSIER → DEMANDE DE RETOUR → CONTRÔLES → MANAGER → APPROUVE/REFUSE → STOCK/FINANCE → AUDIT (§10).
-- La demande ne touche ni au stock ni à la finance : seul l'approbateur (pos.refund), distinct du
-- demandeur, déclenche le retour réel (SalesReturnService), dans la même transaction.
-- =============================================================================
SET search_path TO nexora, public;

CREATE TABLE nexora.sale_return_request (
    id                          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id                  uuid NOT NULL REFERENCES nexora.company(id),
    request_number              text NOT NULL,
    sale_id                     uuid NOT NULL,
    status                      text NOT NULL DEFAULT 'pending_approval',
    reason                      text NOT NULL,
    refund_method               text NOT NULL DEFAULT 'cash',
    refund_financial_account_id uuid REFERENCES nexora.financial_account(id),
    inventory_location_id       uuid NOT NULL REFERENCES nexora.inventory_location(id),
    cash_session_id             uuid,
    currency_id                 uuid NOT NULL REFERENCES nexora.currency(id),
    -- Montant estimé par le serveur à la demande (jamais saisi) ; le montant remboursé réel est
    -- celui du retour exécuté (sale_return.refund_total).
    estimated_refund            nexora.money_amount NOT NULL DEFAULT 0,
    operation_uuid              uuid NOT NULL,
    requested_by                uuid NOT NULL REFERENCES nexora.app_user(id),
    requested_at                timestamptz NOT NULL DEFAULT now(),
    decided_by                  uuid REFERENCES nexora.app_user(id),
    decided_at                  timestamptz,
    decision_note               text,
    sale_return_id              uuid,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    updated_at                  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sale_return_request_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT sale_return_request_number_uk UNIQUE (company_id, request_number),
    CONSTRAINT sale_return_request_operation_uk UNIQUE (operation_uuid),
    CONSTRAINT sale_return_request_sale_fk FOREIGN KEY (sale_id, company_id) REFERENCES nexora.sale(id, company_id),
    CONSTRAINT sale_return_request_return_fk FOREIGN KEY (sale_return_id, company_id) REFERENCES nexora.sale_return(id, company_id),
    CONSTRAINT sale_return_request_session_fk FOREIGN KEY (cash_session_id, company_id) REFERENCES nexora.cash_session(id, company_id),
    CONSTRAINT sale_return_request_status_chk
        CHECK (status IN ('pending_approval', 'rejected', 'executed', 'cancelled')),
    CONSTRAINT sale_return_request_method_chk
        CHECK (refund_method IN ('cash', 'card', 'bank_transfer', 'store_credit', 'credit_note', 'original')),
    CONSTRAINT sale_return_request_reason_chk CHECK (length(btrim(reason)) > 0),
    -- Séparation des responsabilités (§10) : le demandeur ne valide jamais sa propre demande.
    CONSTRAINT sale_return_request_four_eyes_chk CHECK (decided_by IS NULL OR decided_by <> requested_by),
    CONSTRAINT sale_return_request_decided_chk
        CHECK (status NOT IN ('rejected', 'executed') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CONSTRAINT sale_return_request_executed_chk CHECK (status <> 'executed' OR sale_return_id IS NOT NULL),
    CONSTRAINT sale_return_request_rejected_note_chk
        CHECK (status <> 'rejected' OR length(btrim(coalesce(decision_note, ''))) > 0)
);
CREATE INDEX sale_return_request_company_status_idx ON nexora.sale_return_request (company_id, status, requested_at DESC);
CREATE INDEX sale_return_request_sale_idx ON nexora.sale_return_request (sale_id);

CREATE TABLE nexora.sale_return_request_line (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid NOT NULL REFERENCES nexora.company(id),
    request_id        uuid NOT NULL,
    sale_line_id      uuid NOT NULL REFERENCES nexora.sale_line(id),
    quantity          nexora.quantity NOT NULL,
    restock           boolean NOT NULL DEFAULT true,
    estimated_refund  nexora.money_amount NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sale_return_request_line_request_fk FOREIGN KEY (request_id, company_id)
        REFERENCES nexora.sale_return_request(id, company_id) ON DELETE CASCADE,
    CONSTRAINT sale_return_request_line_qty_chk CHECK (quantity > 0)
);
CREATE INDEX sale_return_request_line_request_idx ON nexora.sale_return_request_line (request_id);
CREATE INDEX sale_return_request_line_sale_line_idx ON nexora.sale_return_request_line (sale_line_id);

SELECT nexora.enable_company_rls('nexora.sale_return_request'::regclass);
SELECT nexora.enable_company_rls('nexora.sale_return_request_line'::regclass);

-- --- Audit obligatoire (§22) : retours, demandes, sessions de caisse, ajustements de stock ------
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['sale_return_request', 'sale_return', 'cash_session', 'stock_adjustment'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- --- Notifications Manager (§20) — le lien ouvre directement l'objet concerné -------------
INSERT INTO nexora.message_template (company_id, key, channel, locale, subject, body, description, variables) VALUES
(NULL, 'pos.approval_requested', 'in_app', 'fr',
 '{{request.kind}} à valider — {{request.number}}',
 '{{requester.name}} demande une validation : {{request.summary}}.',
 'Guide §20 — demande de validation Manager (retour, remise exceptionnelle, écart de caisse)',
 '["request.kind","request.number","requester.name","request.summary"]'::jsonb),
(NULL, 'pos.approval_decided', 'in_app', 'fr',
 '{{request.kind}} {{decision}} — {{request.number}}',
 '{{decider.name}} a {{decision}} votre demande. {{decision.note}}',
 'Guide §20 — décision Manager notifiée au demandeur',
 '["request.kind","request.number","decision","decider.name","decision.note"]'::jsonb)
ON CONFLICT ON CONSTRAINT message_template_key_uk DO NOTHING;

SELECT nexora.attach_common_triggers();
