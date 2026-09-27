-- =============================================================================
-- NEXORA ONE — V058 — GED, documents, archivage & signature (Phase 45)
--
-- Deux concepts voisins existent déjà et restent VALIDÉS, volontairement non fusionnés :
--   Phase 30 (V043) : `issued_document` — documents que NEXORA ONE ÉMET (facture/devis/bon…),
--                      numérotés, figés par un hash de contenu, vérifiables publiquement.
--   Phase 44 (V057) : `attachment` — blob minimal (photo/signature) pour la preuve de livraison
--                      et d'intervention terrain, délibérément pauvre (annoncé dès la Phase 44
--                      comme devant être généralisé « par la Phase 45 »).
--   Phase 41 (V054) : `retention_policy` — rétention TECHNIQUE fermée à 4 types (sessions
--                      expirées, défis MFA, jobs terminés, notifications résolues), qui EXCLUT
--                      explicitement toute donnée métier/comptable.
--
-- Cette phase ajoute la bibliothèque documentaire elle-même : des documents REÇUS/IMPORTÉS/
-- SCANNÉS (factures fournisseurs, contrats, justificatifs, pièces comptables), avec
-- classification, versions, liens vers plusieurs objets métier, recherche, verrouillage,
-- signature multi-signataires, partage à lien temporaire, et une politique de RÉTENTION
-- DÉDIÉE AUX DOCUMENTS MÉTIER — précisément ce que `retention_policy` (Phase 41) exclut par
-- construction. `attachment` (Phase 44) n'est pas touchée : elle reste le blob minimal du
-- terrain, `document`/`document_version` sont la bibliothèque versionnée et gouvernée.
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Document — classement & cycle de vie (§3-6, §17-18, §27) =========

CREATE TABLE nexora.document (
    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id             uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    establishment_id       uuid,
    name                   text        NOT NULL,
    document_type          text        NOT NULL,
    category               text,
    tags                   jsonb       NOT NULL DEFAULT '[]'::jsonb,
    reference_type         text,                          -- lien principal (§6) — ex. 'sale','supplier_invoice'
    reference_id           uuid,
    customer_id            uuid,
    supplier_id            uuid,
    fiscal_year            integer,
    document_date          date,
    status                 text        NOT NULL DEFAULT 'draft',
    current_version_id     uuid,                          -- FK ajoutée après création de document_version
    locked                 boolean     NOT NULL DEFAULT false,
    locked_reason          text,
    legal_hold             boolean     NOT NULL DEFAULT false,
    legal_hold_reason      text,
    retention_until        date,
    deletion_requested_at  timestamptz,
    deletion_requested_by  uuid REFERENCES nexora.app_user(id),
    deletion_reason        text,
    created_by             uuid REFERENCES nexora.app_user(id),
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_type_chk CHECK (document_type IN (
        'invoice_sale','invoice_purchase','receipt','credit_note','quote','purchase_order',
        'delivery_note','contract','accounting_voucher','expense_proof','statement','tax_document',
        'customer_document','supplier_document','internal','photo','scan','other')),
    CONSTRAINT document_status_chk CHECK (status IN (
        'draft','pending_review','approved','signed','archived','frozen')),
    CONSTRAINT document_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT document_customer_fk FOREIGN KEY (customer_id, company_id) REFERENCES nexora.customer (id, company_id),
    CONSTRAINT document_supplier_fk FOREIGN KEY (supplier_id, company_id) REFERENCES nexora.supplier (id, company_id)
);
CREATE INDEX document_company_type_idx ON nexora.document (company_id, document_type, status);
CREATE INDEX document_tags_idx         ON nexora.document USING gin (tags);
CREATE INDEX document_reference_idx    ON nexora.document (reference_type, reference_id);
CREATE INDEX document_retention_idx    ON nexora.document (retention_until) WHERE legal_hold = false;

-- ===================== 2. Versions — le fichier lui-même, jamais écrasé (§16, §46) ==========

CREATE TABLE nexora.document_version (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    document_id    uuid        NOT NULL,
    version_no     integer     NOT NULL,
    content        bytea       NOT NULL,
    content_type   text        NOT NULL,
    size_bytes     integer     NOT NULL,
    sha256_hash    text        NOT NULL,
    change_reason  text        NOT NULL DEFAULT '',
    ocr_status     text        NOT NULL DEFAULT 'not_requested',
    ocr_text       text,
    ocr_confidence numeric(5,2),
    ocr_extracted  jsonb,                                 -- {invoiceNumber, date, supplier, amount, tax, currency} §11
    created_by     uuid REFERENCES nexora.app_user(id),
    created_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_version_size_chk CHECK (size_bytes > 0 AND size_bytes <= 26214400),   -- 25 Mo
    CONSTRAINT document_version_ocr_status_chk CHECK (ocr_status IN ('not_requested','pending','completed','unsupported')),
    CONSTRAINT document_version_uk UNIQUE (document_id, version_no),
    CONSTRAINT document_version_document_fk FOREIGN KEY (document_id, company_id)
        REFERENCES nexora.document (id, company_id) ON DELETE CASCADE
);
CREATE INDEX document_version_document_idx ON nexora.document_version (document_id, version_no DESC);
CREATE INDEX document_version_hash_idx     ON nexora.document_version (company_id, sha256_hash);   -- §39 doublons

ALTER TABLE nexora.document ADD CONSTRAINT document_current_version_fk
    FOREIGN KEY (current_version_id) REFERENCES nexora.document_version(id);

-- ===================== 3. Liens multiples vers des objets métier (§6) ======================

CREATE TABLE nexora.document_link (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    document_id  uuid        NOT NULL,
    linked_type  text        NOT NULL,
    linked_id    uuid        NOT NULL,
    created_by   uuid REFERENCES nexora.app_user(id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_link_uk UNIQUE (document_id, linked_type, linked_id),
    CONSTRAINT document_link_document_fk FOREIGN KEY (document_id, company_id)
        REFERENCES nexora.document (id, company_id) ON DELETE CASCADE
);
CREATE INDEX document_link_target_idx ON nexora.document_link (linked_type, linked_id);

-- ===================== 4. Partage à lien temporaire (§31) ===================================

CREATE TABLE nexora.document_share (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    document_id    uuid        NOT NULL,
    token          text        NOT NULL,
    allow_download boolean     NOT NULL DEFAULT true,
    password_hash  text,
    expires_at     timestamptz NOT NULL,
    revoked_at     timestamptz,
    created_by     uuid REFERENCES nexora.app_user(id),
    created_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_share_token_uk UNIQUE (token),
    CONSTRAINT document_share_document_fk FOREIGN KEY (document_id, company_id)
        REFERENCES nexora.document (id, company_id) ON DELETE CASCADE
);
CREATE INDEX document_share_document_idx ON nexora.document_share (document_id);

-- ===================== 5. Signature multi-signataires (§19-22) ==============================

CREATE TABLE nexora.document_signature_request (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    document_id      uuid        NOT NULL,
    status           text        NOT NULL DEFAULT 'pending',
    requested_by     uuid REFERENCES nexora.app_user(id),
    completed_at     timestamptz,
    cancelled_reason text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_signature_request_status_chk CHECK (status IN ('pending','completed','cancelled','expired')),
    CONSTRAINT document_signature_request_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT document_signature_request_document_fk FOREIGN KEY (document_id, company_id)
        REFERENCES nexora.document (id, company_id) ON DELETE CASCADE
);
CREATE INDEX document_signature_request_company_status_idx ON nexora.document_signature_request (company_id, status);

CREATE TABLE nexora.document_signer (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    signature_request_id  uuid        NOT NULL,
    sequence_no           integer     NOT NULL DEFAULT 1,
    signer_type           text        NOT NULL DEFAULT 'user',
    user_id               uuid REFERENCES nexora.app_user(id),
    external_name         text,
    external_email        citext,
    role                  text,
    status                text        NOT NULL DEFAULT 'pending',
    due_at                timestamptz,
    signed_at             timestamptz,
    declined_reason       text,
    proof                 jsonb       NOT NULL DEFAULT '{}'::jsonb,   -- identité déclarée/authentification/hash/version §22
    created_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_signer_type_chk   CHECK (signer_type IN ('user','external')),
    CONSTRAINT document_signer_status_chk CHECK (status IN ('pending','signed','declined')),
    CONSTRAINT document_signer_identity_chk CHECK (
        (signer_type = 'user' AND user_id IS NOT NULL) OR (signer_type = 'external' AND external_name IS NOT NULL)),
    CONSTRAINT document_signer_request_fk FOREIGN KEY (signature_request_id, company_id)
        REFERENCES nexora.document_signature_request (id, company_id) ON DELETE CASCADE
);
CREATE INDEX document_signer_request_idx ON nexora.document_signer (signature_request_id, sequence_no);

-- ===================== 6. Rétention DÉDIÉE aux documents métier (§27-29) ===================
-- Distincte de `retention_policy` (Phase 41), qui exclut explicitement toute donnée métier.

CREATE TABLE nexora.document_retention_policy (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid REFERENCES nexora.company(id),   -- NULL = défaut système, comme sector_profile
    document_type   text,                                 -- NULL = tous types
    retention_years integer     NOT NULL,
    legal_basis     text        NOT NULL DEFAULT '',
    created_by      uuid REFERENCES nexora.app_user(id),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_retention_policy_years_chk CHECK (retention_years > 0),
    CONSTRAINT document_retention_policy_uk UNIQUE NULLS NOT DISTINCT (company_id, document_type)
);

-- ===================== 7. RLS + audit (§33-35, §45) =========================================

SELECT nexora.enable_company_rls('nexora.document'::regclass);
SELECT nexora.enable_company_rls('nexora.document_version'::regclass);
SELECT nexora.enable_company_rls('nexora.document_link'::regclass);
SELECT nexora.enable_company_rls('nexora.document_share'::regclass);
SELECT nexora.enable_company_rls('nexora.document_signature_request'::regclass);
SELECT nexora.enable_company_rls('nexora.document_signer'::regclass);
SELECT nexora.enable_company_rls('nexora.document_retention_policy'::regclass);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'document','document_version','document_link','document_share',
        'document_signature_request','document_signer','document_retention_policy'
    ] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 8. Politiques de rétention système par défaut (§28) =================
-- Durées indicatives par défaut — à ajuster par chaque entreprise selon sa réglementation.

INSERT INTO nexora.document_retention_policy (company_id, document_type, retention_years, legal_basis) VALUES
    (NULL, 'invoice_sale',        10, 'Durée par défaut — à ajuster selon la réglementation applicable'),
    (NULL, 'invoice_purchase',    10, 'Durée par défaut — à ajuster selon la réglementation applicable'),
    (NULL, 'accounting_voucher',  10, 'Durée par défaut — à ajuster selon la réglementation applicable'),
    (NULL, 'tax_document',        10, 'Durée par défaut — à ajuster selon la réglementation applicable'),
    (NULL, 'contract',             5, 'Durée par défaut — à ajuster selon la réglementation applicable'),
    (NULL, NULL,                   3, 'Politique générale par défaut pour tout type non couvert explicitement')
ON CONFLICT ON CONSTRAINT document_retention_policy_uk DO NOTHING;

-- ===================== 9. Job planifié (§27-28) =============================================

INSERT INTO nexora.job_definition (key, name, category, description, owner) VALUES
    ('ged.apply_retention', 'Application des politiques de rétention documentaire', 'business',
        'Calcule la date de conservation de chaque document selon sa politique effective et journalise les documents éligibles à la suppression (§27-28).', 'scheduler'),
    ('ged.expire_signatures', 'Expiration des demandes de signature échues', 'business',
        'Marque expirées les demandes de signature dont un signataire a dépassé sa date limite (§19, §21).', 'scheduler')
ON CONFLICT (key) DO NOTHING;

-- ===================== 10. Permissions (§33, §49) ============================================

INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('ged.view',             'GED — consulter et rechercher les documents',                 'ged', 'view'),
    ('ged.upload',           'GED — importer un document ou une nouvelle version',          'ged', 'upload'),
    ('ged.manage',           'GED — classer, verrouiller, archiver, partager, workflow',    'ged', 'manage'),
    ('ged.sign',             'GED — signer / gérer les demandes de signature',              'ged', 'sign'),
    ('ged.manage_retention', 'GED — politiques de rétention, legal hold, suppression',      'ged', 'manage_retention')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v58(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;
SELECT pg_temp.grant_perms_v58('super_administrator', ARRAY['ged.%']);
SELECT pg_temp.grant_perms_v58('group_administrator', ARRAY['ged.%']);
SELECT pg_temp.grant_perms_v58('owner_direction',     ARRAY['ged.%']);
SELECT pg_temp.grant_perms_v58('chief_accountant',    ARRAY['ged.%']);
SELECT pg_temp.grant_perms_v58('manager',             ARRAY['ged.view','ged.upload','ged.manage','ged.sign']);
SELECT pg_temp.grant_perms_v58('accountant',          ARRAY['ged.view','ged.upload','ged.sign']);
SELECT pg_temp.grant_perms_v58('senior_accountant',   ARRAY['ged.view','ged.upload','ged.sign']);
SELECT pg_temp.grant_perms_v58('purchasing_manager',  ARRAY['ged.view','ged.upload']);
SELECT pg_temp.grant_perms_v58('auditor',             ARRAY['ged.view']);

SELECT nexora.attach_common_triggers();
