-- =============================================================================
-- NEXORA — V066 — Guide fonctionnel développeur, étape 3 : remises exceptionnelles
-- (docs/guide-fonctionnel-conformite.md, écart GF-08).
--
-- PRIX NORMAL → REMISE AUTORISÉE ? → OUI : CONTINUER / NON : DEMANDE MANAGER (§12).
-- Une remise au-delà de la limite du vendeur n'est acceptée à l'encaissement que si une demande
-- approuvée existe pour CE panier (operation_uuid de la vente) et CE produit, d'un pourcentage au
-- moins égal. L'approbation est consommée par la vente (lien consumed_sale_id) : usage unique.
-- =============================================================================
SET search_path TO nexora, public;

CREATE TABLE nexora.pos_discount_request (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid NOT NULL REFERENCES nexora.company(id),
    request_number     text NOT NULL,
    operation_uuid     uuid NOT NULL,
    product_id         uuid NOT NULL REFERENCES nexora.product(id),
    quantity           nexora.quantity NOT NULL,
    currency_id        uuid NOT NULL REFERENCES nexora.currency(id),
    -- Montant brut de la ligne recalculé par le serveur (prix en vigueur × quantité).
    line_gross         nexora.money_amount NOT NULL,
    requested_amount   nexora.money_amount NOT NULL,
    requested_percent  numeric(7,4) NOT NULL,
    reason             text NOT NULL,
    status             text NOT NULL DEFAULT 'pending_approval',
    requested_by       uuid NOT NULL REFERENCES nexora.app_user(id),
    requested_at       timestamptz NOT NULL DEFAULT now(),
    expires_at         timestamptz NOT NULL,
    decided_by         uuid REFERENCES nexora.app_user(id),
    decided_at         timestamptz,
    decision_note      text,
    consumed_sale_id   uuid,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pos_discount_request_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT pos_discount_request_number_uk UNIQUE (company_id, request_number),
    CONSTRAINT pos_discount_request_sale_fk FOREIGN KEY (consumed_sale_id, company_id) REFERENCES nexora.sale(id, company_id),
    CONSTRAINT pos_discount_request_status_chk
        CHECK (status IN ('pending_approval', 'approved', 'rejected', 'consumed', 'expired')),
    CONSTRAINT pos_discount_request_amount_chk
        CHECK (quantity > 0 AND line_gross > 0 AND requested_amount > 0 AND requested_amount <= line_gross),
    CONSTRAINT pos_discount_request_percent_chk CHECK (requested_percent > 0 AND requested_percent <= 100),
    CONSTRAINT pos_discount_request_reason_chk CHECK (length(btrim(reason)) > 0),
    -- Séparation des responsabilités (§12) : le demandeur ne valide jamais sa propre remise.
    CONSTRAINT pos_discount_request_four_eyes_chk CHECK (decided_by IS NULL OR decided_by <> requested_by),
    CONSTRAINT pos_discount_request_decided_chk
        CHECK (status NOT IN ('approved', 'rejected', 'consumed') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CONSTRAINT pos_discount_request_consumed_chk CHECK (status <> 'consumed' OR consumed_sale_id IS NOT NULL)
);
CREATE INDEX pos_discount_request_company_status_idx ON nexora.pos_discount_request (company_id, status, requested_at DESC);
CREATE INDEX pos_discount_request_operation_idx ON nexora.pos_discount_request (operation_uuid, product_id);

SELECT nexora.enable_company_rls('nexora.pos_discount_request'::regclass);

-- §22 : « Remise exceptionnelle : demandeur, validateur, ancien/nouveau montant, date ».
CREATE TRIGGER trg_audit_pos_discount_request AFTER INSERT OR UPDATE OR DELETE ON nexora.pos_discount_request
    FOR EACH ROW EXECUTE FUNCTION nexora.audit_row();

SELECT nexora.attach_common_triggers();
