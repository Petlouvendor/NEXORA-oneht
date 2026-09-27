-- =============================================================================
-- NEXORA — V043 — Facturation & documents commerciaux (Phase 30)
--
-- Le socle existe déjà : `document_sequence` + `nexora.next_document_number()` (numérotation
-- contrôlée par entreprise / type / établissement / année, V015), `issued_document` (registre
-- générique de documents émis, V015 — jusqu'ici inutilisé), `sale` / `sale_return` / `payment`
-- (factures / avoirs / reçus, Phases 6/12), `sales_order` + `OrderService` (commandes clients,
-- Phase 25), `purchase_order` / `goods_receipt` / `supplier_invoice` (Phase 26), relevés
-- client / fournisseur (Phases 26/27), `company_branding` (logo, pied de page, langue, Phase 13).
--
-- Cette migration ajoute le **moteur documentaire** :
--   quote / quote_line   : devis avec cycle brouillon → envoyé → accepté / refusé / expiré → commande (§4-5)
--   issued_document.*     : snapshot de contenu figé + hash, émission / annulation / remplacement,
--                           code de vérification (QR §27-28), langue (§23)
--   document_send         : journal d'envoi (§36, §46)
--   permissions documents.*
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Devis (§4-5) ================================
CREATE TABLE nexora.quote (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    establishment_id uuid,
    customer_id      uuid,
    document_number  text        NOT NULL,
    currency_id      uuid        NOT NULL REFERENCES nexora.currency(id),
    status           text        NOT NULL DEFAULT 'draft',
    version          integer     NOT NULL DEFAULT 1,
    quote_date       date        NOT NULL DEFAULT current_date,
    valid_until      date,
    subtotal         nexora.money_amount NOT NULL DEFAULT 0,
    discount_total   nexora.money_amount NOT NULL DEFAULT 0,
    tax_total        nexora.money_amount NOT NULL DEFAULT 0,
    total            nexora.money_amount NOT NULL DEFAULT 0,
    terms            text,
    notes            text,
    sent_at          timestamptz,
    decided_at       timestamptz,
    converted_order_id uuid,
    created_by       uuid REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT quote_status_chk CHECK (status IN ('draft','sent','accepted','rejected','expired','converted','cancelled')),
    CONSTRAINT quote_totals_chk CHECK (subtotal >= 0 AND tax_total >= 0 AND total >= 0),
    CONSTRAINT quote_company_number_uk UNIQUE (company_id, document_number),
    CONSTRAINT quote_company_id_uk     UNIQUE (id, company_id),
    CONSTRAINT quote_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT quote_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);
CREATE INDEX quote_company_status_idx ON nexora.quote (company_id, status, quote_date DESC);
CREATE INDEX quote_customer_idx       ON nexora.quote (customer_id);

CREATE TABLE nexora.quote_line (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    quote_id     uuid        NOT NULL,
    company_id   uuid        NOT NULL,
    product_id   uuid        NOT NULL,
    description  text        NOT NULL DEFAULT '',
    quantity     nexora.quantity NOT NULL,
    unit_price   nexora.money_amount NOT NULL DEFAULT 0,
    discount     nexora.money_amount NOT NULL DEFAULT 0,
    tax_id       uuid,
    tax_amount   nexora.money_amount NOT NULL DEFAULT 0,
    line_total   nexora.money_amount NOT NULL DEFAULT 0,
    line_number  integer     NOT NULL DEFAULT 1,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT quote_line_qty_chk CHECK (quantity > 0),
    CONSTRAINT quote_line_quote_fk FOREIGN KEY (quote_id, company_id)
        REFERENCES nexora.quote (id, company_id) ON DELETE CASCADE,
    CONSTRAINT quote_line_product_fk FOREIGN KEY (product_id, company_id)
        REFERENCES nexora.product (id, company_id)
);
CREATE INDEX quote_line_quote_idx ON nexora.quote_line (quote_id);

-- ===================== 2. Document émis : snapshot + vérification (§2, §19, §27-33) ====
ALTER TABLE nexora.issued_document
    ADD COLUMN IF NOT EXISTS content            jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS content_hash       text,
    ADD COLUMN IF NOT EXISTS total_net          nexora.money_amount NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS total_tax          nexora.money_amount NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS language            text NOT NULL DEFAULT 'fr-FR',
    ADD COLUMN IF NOT EXISTS issued_at           timestamptz,
    ADD COLUMN IF NOT EXISTS issued_by           uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS cancelled_at        timestamptz,
    ADD COLUMN IF NOT EXISTS cancel_reason       text,
    ADD COLUMN IF NOT EXISTS cancelled_by        uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS replaced_by_document_id uuid,
    ADD COLUMN IF NOT EXISTS verification_code   text;

ALTER TABLE nexora.issued_document DROP CONSTRAINT IF EXISTS issued_document_type_chk;
ALTER TABLE nexora.issued_document ADD CONSTRAINT issued_document_type_chk CHECK (document_type IN (
    'quote','proforma','invoice','deposit_invoice','final_invoice','delivery_note','goods_receipt',
    'purchase_order','credit_note','debit_note','customer_statement','supplier_statement','receipt','other'));

ALTER TABLE nexora.issued_document DROP CONSTRAINT IF EXISTS issued_document_status_chk;
ALTER TABLE nexora.issued_document ADD CONSTRAINT issued_document_status_chk CHECK (status IN (
    'draft','issued','sent','accepted','rejected','expired','cancelled','converted','replaced'));

CREATE UNIQUE INDEX IF NOT EXISTS issued_document_verification_uk
    ON nexora.issued_document (verification_code) WHERE verification_code IS NOT NULL;
CREATE INDEX IF NOT EXISTS issued_document_party_idx ON nexora.issued_document (party_type, party_id);

-- Clé composite (id, company_id) pour les FK inter-tenants (convention NEXORA §31).
ALTER TABLE nexora.issued_document DROP CONSTRAINT IF EXISTS issued_document_company_id_uk;
ALTER TABLE nexora.issued_document ADD CONSTRAINT issued_document_company_id_uk UNIQUE (id, company_id);

-- ===================== 3. Journal d'envoi (§36, §46) ==================
CREATE TABLE nexora.document_send (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid        NOT NULL REFERENCES nexora.company(id),
    issued_document_id uuid        NOT NULL,
    channel            text        NOT NULL,
    recipient          text,
    subject            text,
    message            text,
    status             text        NOT NULL DEFAULT 'queued',
    error              text,
    sent_at            timestamptz,
    sent_by            uuid REFERENCES nexora.app_user(id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_send_channel_chk CHECK (channel IN ('email','link','print','sms','download','portal')),
    CONSTRAINT document_send_status_chk CHECK (status IN ('queued','sent','failed')),
    CONSTRAINT document_send_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT document_send_document_fk FOREIGN KEY (issued_document_id, company_id)
        REFERENCES nexora.issued_document (id, company_id) ON DELETE CASCADE
);
CREATE INDEX document_send_document_idx ON nexora.document_send (issued_document_id, created_at DESC);

-- ===================== 4. RLS (§22, §55) & audit (§46) ================
SELECT nexora.enable_company_rls('nexora.quote'::regclass);
SELECT nexora.enable_company_rls('nexora.quote_line'::regclass);
SELECT nexora.enable_company_rls('nexora.document_send'::regclass);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['quote','quote_line','document_send','issued_document'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 5. Permissions (§47) ==========================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('documents.view',            'Documents — consulter & rechercher',        'documents', 'view'),
    ('documents.create_quote',    'Documents — créer / envoyer un devis',      'documents', 'create_quote'),
    ('documents.issue',           'Documents — émettre facture / avoir / reçu', 'documents', 'issue'),
    ('documents.cancel',          'Documents — annuler un document émis',       'documents', 'cancel'),
    ('documents.send',            'Documents — envoyer / partager un document', 'documents', 'send'),
    ('documents.manage_sequence', 'Documents — gérer la numérotation',          'documents', 'manage_sequence')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v43(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v43('super_administrator', ARRAY['documents.%']);
SELECT pg_temp.grant_perms_v43('group_administrator', ARRAY['documents.%']);
SELECT pg_temp.grant_perms_v43('owner_direction',     ARRAY['documents.%']);
SELECT pg_temp.grant_perms_v43('manager',             ARRAY['documents.%']);
SELECT pg_temp.grant_perms_v43('chief_accountant',    ARRAY['documents.view','documents.issue','documents.cancel','documents.send']);
SELECT pg_temp.grant_perms_v43('accountant',          ARRAY['documents.view','documents.issue','documents.send']);
SELECT pg_temp.grant_perms_v43('salesperson',         ARRAY['documents.view','documents.create_quote','documents.send']);
SELECT pg_temp.grant_perms_v43('cashier',             ARRAY['documents.view','documents.issue']);

SELECT nexora.attach_common_triggers();
