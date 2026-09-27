-- =============================================================================
-- NEXORA — V059 — API, Intégrations & Écosystème : Marketplace partenaires (Phase 46)
--
-- Le socle d'ouverture existe déjà et reste VALIDÉ par cette phase :
--   Phase 12 (V031) : `integration_connector` (connecteurs remplaçables §35-36), `payment_intent`
--     (abstraction paiement §21-22), `webhook_event` (entrants HMAC §13-14), `outbound_webhook` /
--     `outbound_webhook_delivery` (sortants signés + retry/backoff/dead-letter §13-14, bac à sable
--     déjà présent pour l'hôte `sim.webhook.local` §16), rapprochement bancaire (§23), centre
--     d'intégrations (§40)
--   Phase 35 (V048) : `api_key` — authentification machine-à-machine, scopes ⊆ permissions du
--     propriétaire, environnement test/live (§16), rotation/révocation/expiration, quota par
--     minute (§9) — couvre déjà l'essentiel des « service accounts » (§8) et de l'authentification
--     API par clé (§6-7)
--   Phase 3 : `/api/v1`, erreurs RFC 7807 + correlation id (§12), `IdempotencyFilter` (§10)
--
-- Cette migration ajoute la couche qui manquait réellement (§41-48) : un **catalogue d'apps
-- partenaires** gouverné par NEXORA (curation/review, jamais un accès direct à la base), une
-- **installation par entreprise** à permissions explicitement accordées (jamais héritées), et le
-- raccordement à `api_key` pour qu'une app installée obtienne un accès API réel — réutilisant
-- entièrement le moteur de clés de la Phase 35 plutôt que d'en construire un second (§46, §51).
-- Ajoute aussi un compteur d'usage quotidien par clé pour un vrai « portail développeur » (§47-48).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Catalogue marketplace (§41-42, §45) — table plateforme, sans RLS
-- entreprise : une seule vitrine pour tout NEXORA ONE, comme `platform_plan` (Phase 38) ou
-- `kpi_definition` (Phase 42). La curation reste un acte NEXORA (`platform.manage_marketplace`),
-- jamais un accès libre d'un tiers à sa propre fiche.
CREATE TABLE nexora.marketplace_app (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    slug                text        NOT NULL,
    name                text        NOT NULL,
    publisher_name      text        NOT NULL,
    publisher_contact   text,
    category            text        NOT NULL,
    certification       text        NOT NULL DEFAULT 'community',
    status              text        NOT NULL DEFAULT 'draft',
    version             text        NOT NULL DEFAULT '1.0',
    description         text,
    requested_scopes    jsonb       NOT NULL DEFAULT '[]'::jsonb,   -- codes de permission demandés (§44)
    pricing_model       text        NOT NULL DEFAULT 'free',
    price_amount        nexora.money_amount,
    price_currency_code text        NOT NULL DEFAULT 'XOF',
    homepage_url        text,
    privacy_policy_url  text,
    support_contact     text,
    submitted_by        uuid        REFERENCES nexora.app_user(id),   -- NULL = app officielle NEXORA
    reviewed_by         uuid        REFERENCES nexora.app_user(id),
    reviewed_at         timestamptz,
    review_notes        text,
    published_at        timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT marketplace_app_category_chk CHECK (category IN (
        'payment','bank','mobile_money','accounting_export','ecommerce','crm','erp','delivery',
        'sms_email','messaging','storage','signature','ocr','pos_terminal','other')),
    CONSTRAINT marketplace_app_certification_chk CHECK (certification IN ('official','certified','community')),
    CONSTRAINT marketplace_app_status_chk CHECK (status IN ('draft','pending_review','approved','rejected','suspended')),
    CONSTRAINT marketplace_app_pricing_chk CHECK (pricing_model IN ('free','paid','subscription','usage')),
    CONSTRAINT marketplace_app_price_chk CHECK (
        (pricing_model = 'free' AND price_amount IS NULL) OR (pricing_model <> 'free' AND price_amount IS NOT NULL AND price_amount >= 0)),
    CONSTRAINT marketplace_app_slug_uk UNIQUE (slug)
);
CREATE INDEX marketplace_app_catalogue_idx ON nexora.marketplace_app (status, category, created_at DESC);

-- ===================== 2. Installation par entreprise (§43-44, §56) — RLS entreprise standard.
-- `granted_scopes` est le périmètre RÉELLEMENT autorisé par l'entreprise, toujours ⊆
-- `marketplace_app.requested_scopes` (contrôlé par le service, jamais par une contrainte SQL —
-- même logique que `api_key.scopes ⊆ permissions du propriétaire`, Phase 35 §13).
CREATE TABLE nexora.app_installation (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL REFERENCES nexora.company(id),
    marketplace_app_id  uuid        NOT NULL REFERENCES nexora.marketplace_app(id),
    status              text        NOT NULL DEFAULT 'installed',
    granted_scopes      jsonb       NOT NULL DEFAULT '[]'::jsonb,
    config              jsonb       NOT NULL DEFAULT '{}'::jsonb,
    installed_by        uuid        REFERENCES nexora.app_user(id),
    installed_at        timestamptz NOT NULL DEFAULT now(),
    disabled_at         timestamptz,
    uninstalled_at      timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT app_installation_status_chk CHECK (status IN ('installed','disabled','uninstalled')),
    CONSTRAINT app_installation_company_app_uk UNIQUE (company_id, marketplace_app_id),
    CONSTRAINT app_installation_company_id_uk   UNIQUE (id, company_id)   -- cible d'une FK composite (api_key)
);
CREATE INDEX app_installation_company_idx ON nexora.app_installation (company_id, status);

-- ===================== 3. Raccordement à api_key (§8, §46) — une app installée obtient un accès
-- API réel via une clé ordinaire, jamais un second mécanisme d'authentification. Réutilise
-- intégralement rotation / révocation / quota / environnement de la Phase 35.
ALTER TABLE nexora.api_key ADD COLUMN app_installation_id uuid;
-- Une FK composite (id, company_id) exige que la table cible déclare sa propre unicité (id, company_id)
-- — leçon retenue de la Phase 45 (`document_signature_request_company_id_uk`).
ALTER TABLE nexora.api_key ADD CONSTRAINT api_key_company_id_uk UNIQUE (id, company_id);
ALTER TABLE nexora.api_key ADD CONSTRAINT api_key_app_installation_fk
    FOREIGN KEY (app_installation_id, company_id) REFERENCES nexora.app_installation (id, company_id);

-- ===================== 4. Usage quotidien par clé (§47-48) — portail développeur : nombre
-- d'appels et de refus par quota, par jour. Compteur d'ajout seul, jamais audité individuellement
-- (même statut que `report_execution` ou `assistant_query`).
CREATE TABLE nexora.api_key_daily_usage (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL,
    api_key_id          uuid        NOT NULL,
    usage_date          date        NOT NULL,
    request_count       integer     NOT NULL DEFAULT 0,
    rate_limited_count  integer     NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT api_key_daily_usage_uk UNIQUE (api_key_id, usage_date),
    CONSTRAINT api_key_daily_usage_key_fk FOREIGN KEY (api_key_id, company_id)
        REFERENCES nexora.api_key (id, company_id) ON DELETE CASCADE
);
CREATE INDEX api_key_daily_usage_key_idx ON nexora.api_key_daily_usage (api_key_id, usage_date DESC);

-- ===================== 5. RLS + audit ========================================
SELECT nexora.enable_company_rls('nexora.app_installation'::regclass);
SELECT nexora.enable_company_rls('nexora.api_key_daily_usage'::regclass);
-- `marketplace_app` : pas de RLS entreprise (table plateforme, comme `platform_plan`) ; l'accès en
-- écriture est filtré par `platform.manage_marketplace`, la lecture publique par le service (statut
-- `approved` uniquement pour un appelant sans ce privilège).

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['marketplace_app','app_installation'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 6. Permissions (§7-8, §44, §57) =======================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('platform.manage_marketplace', 'Plateforme — cataloguer et réviser les apps marketplace', 'platform', 'manage_marketplace'),
    ('integrations.manage_apps',    'Intégrations — installer et configurer les apps marketplace', 'integrations', 'manage_apps')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v59(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

-- Curation marketplace : réservée au personnel plateforme (Phase 38 §36).
SELECT pg_temp.grant_perms_v59('super_administrator', ARRAY['platform.manage_marketplace']);
SELECT pg_temp.grant_perms_v59('platform_admin',      ARRAY['platform.manage_marketplace']);

-- Installation côté entreprise : même distribution que les autres droits `integrations.*` (V031).
SELECT pg_temp.grant_perms_v59('super_administrator', ARRAY['integrations.manage_apps']);
SELECT pg_temp.grant_perms_v59('group_administrator', ARRAY['integrations.manage_apps']);
SELECT pg_temp.grant_perms_v59('owner_direction',     ARRAY['integrations.manage_apps']);
SELECT pg_temp.grant_perms_v59('manager',             ARRAY['integrations.manage_apps']);

-- ===================== 7. Amorçage — 3 apps officielles NEXORA (démonstration du catalogue) ====
INSERT INTO nexora.marketplace_app
    (slug, name, publisher_name, category, certification, status, version, description,
     requested_scopes, pricing_model, price_amount, price_currency_code, reviewed_at, published_at)
VALUES
    ('nexora-mobile-money-connect', 'Nexora Connect — Mobile Money', 'NEXORA', 'mobile_money', 'official', 'approved', '1.0',
     'Connecteur officiel Mobile Money (paiement, confirmation, rapprochement) — s''appuie sur la couche paiement Phase 12.',
     '["integrations.payments"]'::jsonb, 'free', NULL, 'XOF', now(), now()),
    ('nexora-export-comptable', 'Nexora Export Comptable', 'NEXORA', 'accounting_export', 'official', 'approved', '1.0',
     'Export du plan comptable, journaux et balance vers un cabinet ou un logiciel comptable externe.',
     '["accounting.view","accounting.export"]'::jsonb, 'free', NULL, 'XOF', now(), now()),
    ('nexora-ecommerce-sync', 'Nexora Sync E-commerce', 'NEXORA', 'ecommerce', 'official', 'approved', '1.0',
     'Synchronisation catalogue, stock et commandes avec une boutique en ligne (§27).',
     '["catalog.view","inventory.view","pos.manage_order"]'::jsonb, 'free', NULL, 'XOF', now(), now())
ON CONFLICT (slug) DO NOTHING;

SELECT nexora.attach_common_triggers();
