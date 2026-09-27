-- =============================================================================
-- NEXORA — V051 — Administration SaaS & monétisation : la plateforme qui vend NEXORA (Phase 38)
--
-- Le socle applicatif (le produit vendu) reste VALIDÉ ; cette phase ajoute la **couche
-- plateforme** — commerciale et distincte de la comptabilité des entreprises clientes (§2, §43-44) :
--
--   Réutilisé :
--     Phase 3   : API `/api/v1` versionnée, deny-by-default, JWT + MFA (Phase 33) pour les admins (§53)
--     Phase 11  : `audit_log` immuable (trigger) — sert aussi l'audit billing (§48)
--     Phase 12  : couche de paiement remplaçable + webhooks HMAC idempotents (§15, §54)
--     Phase 34  : `INotificationService` — les notifications SaaS (§47) passent par le même moteur
--     Phase 35  : `api_key` — modèle « secret haché, jamais ré-affiché » repris pour les licences offline (§28)
--     Phase 37  : `deployment` — précédent d'une table **plateforme** sans RLS entreprise
--
-- Tables plateforme (PAS de company_id, PAS de RLS entreprise — l'accès est filtré par les
-- permissions `platform.*` réservées aux rôles plateforme §35-36). L'isolation stricte des
-- **données métier** des clients (§8, §53) est déjà assurée par la RLS des phases 2-37 ; la
-- facturation de NEXORA est par nature transverse aux locataires.
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Catalogue : plans & add-ons (§4-6, §20, §39) ====

CREATE TABLE nexora.platform_plan (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code          text        NOT NULL,                     -- « starter », « business »… (§4)
    name          text        NOT NULL,
    tier          text        NOT NULL DEFAULT 'starter',
    description   text,
    price_amount  nexora.money_amount NOT NULL DEFAULT 0,   -- prix catalogue (§5)
    currency_code text        NOT NULL DEFAULT 'USD',        -- devise de prix — propre à NEXORA (§42)
    period        text        NOT NULL DEFAULT 'monthly',    -- monthly | annual (§3)
    trial_days    integer     NOT NULL DEFAULT 0,            -- essai gratuit par défaut (§12)
    quotas        jsonb       NOT NULL DEFAULT '{}'::jsonb,  -- { users, companies, establishments, terminals, transactions, storage_mb, api_requests, webhooks, exports, documents, automations } (§5, §21)
    features      jsonb       NOT NULL DEFAULT '{}'::jsonb,  -- { module_code: "included" | "optional" | "unavailable" } (§6)
    is_public     boolean     NOT NULL DEFAULT true,         -- visible au catalogue self-service (§39)
    sort_order    integer     NOT NULL DEFAULT 0,
    status        text        NOT NULL DEFAULT 'active',     -- active | retired
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_plan_code_uk   UNIQUE (code),
    CONSTRAINT platform_plan_tier_chk  CHECK (tier IN ('starter','business','professional','enterprise','custom')),
    CONSTRAINT platform_plan_period_chk CHECK (period IN ('monthly','annual')),
    CONSTRAINT platform_plan_status_chk CHECK (status IN ('active','retired')),
    CONSTRAINT platform_plan_trial_chk CHECK (trial_days BETWEEN 0 AND 365)
);

CREATE TABLE nexora.platform_addon (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code          text        NOT NULL,
    name          text        NOT NULL,
    kind          text        NOT NULL DEFAULT 'feature',    -- users|companies|establishments|terminals|storage|api|connectors|support|feature (§20)
    quota_key     text,                                       -- quota incrémenté par unité (NULL = simple option)
    quota_delta   bigint      NOT NULL DEFAULT 0,             -- incrément de quota par unité achetée
    unit_price    nexora.money_amount NOT NULL DEFAULT 0,
    currency_code text        NOT NULL DEFAULT 'USD',
    period        text        NOT NULL DEFAULT 'monthly',
    is_public     boolean     NOT NULL DEFAULT true,
    status        text        NOT NULL DEFAULT 'active',
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_addon_code_uk UNIQUE (code),
    CONSTRAINT platform_addon_period_chk CHECK (period IN ('monthly','annual')),
    CONSTRAINT platform_addon_status_chk CHECK (status IN ('active','retired'))
);

-- ===================== 2. Compte client / organisation (§7-9) =============

CREATE TABLE nexora.platform_customer (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_ref         text        NOT NULL,               -- identifiant client lisible « NXC-000123 » (§7)
    legal_name           text        NOT NULL,
    primary_contact_name text,
    primary_contact_email text,
    billing_email        text,
    phone                text,
    address_line         text,
    city                 text,
    country_code         text        NOT NULL DEFAULT 'HT',  -- pays de facturation (§41)
    billing_currency     text        NOT NULL DEFAULT 'USD', -- devise de facture (§42)
    tax_id               text,                                -- numéro fiscal si nécessaire (§41)
    tax_exempt           boolean     NOT NULL DEFAULT false, -- exonération selon règles applicables (§41)
    commercial_terms     text,                                -- conditions commerciales (§7, §26)
    status               text        NOT NULL DEFAULT 'active', -- active | suspended | closed (§7, §30-31)
    created_by           uuid REFERENCES nexora.app_user(id),
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_customer_ref_uk  UNIQUE (customer_ref),
    CONSTRAINT platform_customer_status_chk CHECK (status IN ('active','suspended','closed'))
);

-- Quelles entreprises (locataires) appartiennent à ce client (§9). Une entreprise = un seul client.
CREATE TABLE nexora.platform_customer_company (
    customer_id  uuid        NOT NULL REFERENCES nexora.platform_customer(id) ON DELETE CASCADE,
    company_id   uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    linked_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (customer_id, company_id),
    CONSTRAINT platform_customer_company_uk UNIQUE (company_id)
);

-- ===================== 3. Abonnement (§10-11, §19, §29) ==================

CREATE TABLE nexora.subscription (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id          uuid        NOT NULL REFERENCES nexora.platform_customer(id),
    plan_id              uuid        NOT NULL REFERENCES nexora.platform_plan(id),
    status               text        NOT NULL DEFAULT 'trialing', -- §10
    period               text        NOT NULL DEFAULT 'monthly',
    price_amount         nexora.money_amount NOT NULL DEFAULT 0,  -- prix figé à la souscription (§5, §26 sur-mesure)
    currency_code        text        NOT NULL DEFAULT 'USD',
    quotas_override      jsonb,                                    -- surcharge négociée (Enterprise §26) ; NULL = quotas du plan
    trial_ends_at        timestamptz,                              -- (§12)
    current_period_start timestamptz NOT NULL DEFAULT now(),
    current_period_end   timestamptz NOT NULL DEFAULT (now() + interval '1 month'),
    auto_renew           boolean     NOT NULL DEFAULT true,        -- renouvellement programmé (§10-11)
    cancel_at_period_end boolean     NOT NULL DEFAULT false,       -- annulation en fin de période (§31)
    canceled_at          timestamptz,
    cancel_reason        text,
    grace_ends_at        timestamptz,                              -- échéance de la période de grâce (§16-17)
    suspended_at         timestamptz,
    suspend_reason       text,
    payment_failures     integer     NOT NULL DEFAULT 0,           -- tentatives échouées consécutives (§16)
    activated_at         timestamptz,
    created_by           uuid REFERENCES nexora.app_user(id),
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT subscription_status_chk CHECK (status IN
        ('trialing','active','past_due','grace','suspended','canceled','expired')),
    CONSTRAINT subscription_period_chk CHECK (period IN ('monthly','annual'))
);
CREATE INDEX subscription_customer_idx ON nexora.subscription (customer_id, status);
CREATE INDEX subscription_renewal_idx  ON nexora.subscription (current_period_end)
    WHERE status IN ('active','past_due','grace','trialing');
-- Un client n'a qu'un seul abonnement vivant à la fois.
CREATE UNIQUE INDEX subscription_one_live_idx ON nexora.subscription (customer_id)
    WHERE status IN ('trialing','active','past_due','grace','suspended');

CREATE TABLE nexora.subscription_addon (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    subscription_id  uuid        NOT NULL REFERENCES nexora.subscription(id) ON DELETE CASCADE,
    addon_id         uuid        NOT NULL REFERENCES nexora.platform_addon(id),
    quantity         integer     NOT NULL DEFAULT 1,
    unit_price       nexora.money_amount NOT NULL DEFAULT 0,   -- figé (§5)
    currency_code    text        NOT NULL DEFAULT 'USD',
    added_at         timestamptz NOT NULL DEFAULT now(),
    removed_at       timestamptz,
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT subscription_addon_qty_chk CHECK (quantity >= 1)
);
CREATE INDEX subscription_addon_sub_idx ON nexora.subscription_addon (subscription_id) WHERE removed_at IS NULL;

-- ===================== 4. Promotions / coupons (§40) ====================

CREATE TABLE nexora.platform_coupon (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code             text        NOT NULL,
    description      text,
    kind             text        NOT NULL DEFAULT 'percent',   -- percent | fixed
    value            numeric(12,4) NOT NULL DEFAULT 0,         -- % (0-100) ou montant fixe dans currency_code
    currency_code    text,
    duration         text        NOT NULL DEFAULT 'once',      -- once | first_period | forever (§40)
    max_redemptions  integer,                                   -- NULL = illimité
    redeemed_count   integer     NOT NULL DEFAULT 0,
    expires_at       timestamptz,
    status           text        NOT NULL DEFAULT 'active',    -- active | disabled
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_coupon_code_uk UNIQUE (code),
    CONSTRAINT platform_coupon_kind_chk CHECK (kind IN ('percent','fixed')),
    CONSTRAINT platform_coupon_duration_chk CHECK (duration IN ('once','first_period','forever')),
    CONSTRAINT platform_coupon_status_chk CHECK (status IN ('active','disabled')),
    CONSTRAINT platform_coupon_value_chk CHECK (value >= 0 AND (kind <> 'percent' OR value <= 100))
);

-- ===================== 5. Facturation NEXORA (§13, §41, §43-44) =========
-- Distincte de la comptabilité des clients. Numérotation par séquence dédiée.

CREATE SEQUENCE IF NOT EXISTS nexora.platform_invoice_seq;
CREATE SEQUENCE IF NOT EXISTS nexora.platform_customer_seq;
CREATE SEQUENCE IF NOT EXISTS nexora.support_ticket_seq;

CREATE TABLE nexora.platform_invoice (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    number           text        NOT NULL,                    -- « NXR-2026-000042 » (§13)
    customer_id      uuid        NOT NULL REFERENCES nexora.platform_customer(id),
    subscription_id  uuid        REFERENCES nexora.subscription(id),
    kind             text        NOT NULL DEFAULT 'invoice',  -- invoice | credit_note | receipt (§13)
    parent_invoice_id uuid       REFERENCES nexora.platform_invoice(id),  -- avoir → facture d'origine
    status           text        NOT NULL DEFAULT 'issued',   -- draft | issued | paid | overdue | void | credited (§10, §13)
    currency_code    text        NOT NULL DEFAULT 'USD',
    issue_date       date        NOT NULL DEFAULT current_date,
    due_date         date        NOT NULL DEFAULT (current_date + 14),
    period_start     date,
    period_end       date,
    subtotal_amount  nexora.money_amount NOT NULL DEFAULT 0,
    discount_amount  nexora.money_amount NOT NULL DEFAULT 0,  -- remise commerciale / coupon (§13, §40)
    tax_amount       nexora.money_amount NOT NULL DEFAULT 0,  -- taxe applicable (§41)
    tax_rate         numeric(9,6) NOT NULL DEFAULT 0,
    total_amount     nexora.money_amount NOT NULL DEFAULT 0,
    paid_amount      nexora.money_amount NOT NULL DEFAULT 0,
    coupon_code      text,
    notes            text,
    issued_at        timestamptz,
    paid_at          timestamptz,
    voided_at        timestamptz,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_invoice_number_uk UNIQUE (number),
    CONSTRAINT platform_invoice_kind_chk CHECK (kind IN ('invoice','credit_note','receipt')),
    CONSTRAINT platform_invoice_status_chk CHECK (status IN ('draft','issued','paid','overdue','void','credited'))
);
CREATE INDEX platform_invoice_customer_idx ON nexora.platform_invoice (customer_id, issue_date DESC);
CREATE INDEX platform_invoice_open_idx     ON nexora.platform_invoice (due_date) WHERE status IN ('issued','overdue');

CREATE TABLE nexora.platform_invoice_line (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    invoice_id   uuid        NOT NULL REFERENCES nexora.platform_invoice(id) ON DELETE CASCADE,
    line_type    text        NOT NULL DEFAULT 'plan',          -- plan | addon | proration | usage | discount | tax (§14, §24)
    description  text        NOT NULL,
    quantity     numeric(20,6) NOT NULL DEFAULT 1,
    unit_price   nexora.money_amount NOT NULL DEFAULT 0,
    amount       nexora.money_amount NOT NULL DEFAULT 0,       -- peut être négatif (prorata downgrade, remise)
    sort_order   integer     NOT NULL DEFAULT 0,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_invoice_line_type_chk CHECK (line_type IN ('plan','addon','proration','usage','discount','tax'))
);
CREATE INDEX platform_invoice_line_invoice_idx ON nexora.platform_invoice_line (invoice_id, sort_order);

-- ===================== 6. Paiements NEXORA (§15-16, §54) ================
-- Couche interchangeable ; les événements de facturation restent séparés des écritures (§44).

CREATE TABLE nexora.platform_payment (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id       uuid        REFERENCES nexora.platform_customer(id),   -- NULL possible : événement webhook non rattaché → revue manuelle (§54)
    invoice_id        uuid        REFERENCES nexora.platform_invoice(id),
    method            text        NOT NULL DEFAULT 'card',      -- card | bank_transfer | mobile_money | manual (§15)
    provider          text,                                      -- fournisseur PSP / passerelle
    provider_ref      text,                                      -- référence externe (§15)
    provider_event_id text,                                      -- identifiant d'événement webhook — idempotence (§54)
    amount            nexora.money_amount NOT NULL DEFAULT 0,
    currency_code     text        NOT NULL DEFAULT 'USD',
    status            text        NOT NULL DEFAULT 'pending',   -- pending | succeeded | failed | refunded (§54 état explicite)
    failure_reason    text,
    review_required   boolean     NOT NULL DEFAULT false,       -- événement ambigu → revue manuelle, jamais de suspension auto (§54)
    is_manual         boolean     NOT NULL DEFAULT false,       -- saisi puis validé par un administrateur (§15)
    confirmed_by      uuid REFERENCES nexora.app_user(id),
    received_at       timestamptz NOT NULL DEFAULT now(),
    reconciled_at     timestamptz,                               -- réconciliation quotidienne (§54)
    refunded_amount   nexora.money_amount NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_payment_method_chk CHECK (method IN ('card','bank_transfer','mobile_money','manual')),
    CONSTRAINT platform_payment_status_chk CHECK (status IN ('pending','succeeded','failed','refunded')),
    CONSTRAINT platform_payment_event_uk UNIQUE (provider_event_id)   -- NULLs autorisés (paiements manuels)
);
CREATE INDEX platform_payment_customer_idx ON nexora.platform_payment (customer_id, received_at DESC);
CREATE INDEX platform_payment_review_idx   ON nexora.platform_payment (review_required) WHERE review_required;

-- Méthodes de paiement enregistrées (jeton PSP uniquement — aucune donnée carte §53).
CREATE TABLE nexora.platform_payment_method (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id    uuid        NOT NULL REFERENCES nexora.platform_customer(id) ON DELETE CASCADE,
    method         text        NOT NULL DEFAULT 'card',
    provider       text,
    provider_token text,                                         -- jeton du PSP — jamais le PAN (§53)
    brand          text,                                          -- « visa », « mastercard »…
    last4          text,
    exp_month      integer,
    exp_year       integer,
    is_default     boolean     NOT NULL DEFAULT false,
    status         text        NOT NULL DEFAULT 'active',        -- active | expired | removed
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_payment_method_status_chk CHECK (status IN ('active','expired','removed')),
    CONSTRAINT platform_payment_method_last4_chk CHECK (last4 IS NULL OR last4 ~ '^[0-9]{4}$')
);
CREATE INDEX platform_payment_method_customer_idx ON nexora.platform_payment_method (customer_id) WHERE status = 'active';

-- ===================== 7. Usage & quotas (§21-24) ======================

CREATE TABLE nexora.usage_counter (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id   uuid        NOT NULL REFERENCES nexora.platform_customer(id) ON DELETE CASCADE,
    resource      text        NOT NULL,   -- users|companies|establishments|terminals|transactions|storage_mb|api_requests|webhooks|exports|documents|automations (§21)
    period_start  date        NOT NULL DEFAULT date_trunc('month', now())::date,
    value         bigint      NOT NULL DEFAULT 0,
    last_alert_pct integer    NOT NULL DEFAULT 0,   -- dernier seuil d'alerte notifié (§23 : 80/90/100)
    updated_at    timestamptz NOT NULL DEFAULT now(),
    created_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT usage_counter_uk UNIQUE (customer_id, resource, period_start),
    CONSTRAINT usage_counter_value_chk CHECK (value >= 0)
);
CREATE INDEX usage_counter_customer_idx ON nexora.usage_counter (customer_id, period_start DESC);

-- ===================== 8. Licences (§27-29) ============================

CREATE TABLE nexora.platform_license (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id    uuid        NOT NULL REFERENCES nexora.platform_customer(id) ON DELETE CASCADE,
    subscription_id uuid       REFERENCES nexora.subscription(id),
    scope          text        NOT NULL DEFAULT 'organization',  -- organization | company | user | terminal | module (§27)
    subject_ref    text,                                          -- id d'entreprise / d'utilisateur / code module concerné
    status         text        NOT NULL DEFAULT 'active',        -- active | expired | revoked (§27)
    grace_days     integer     NOT NULL DEFAULT 7,               -- tolérance offline (§28)
    activated_at   timestamptz NOT NULL DEFAULT now(),
    expires_at     timestamptz NOT NULL DEFAULT (now() + interval '35 days'),  -- durée de validité limitée (§28)
    last_issued_at timestamptz,                                   -- dernier jeton offline émis
    token_hash     text,                                          -- SHA-256 du dernier jeton (traçabilité, pas le secret §28)
    revoked_at     timestamptz,
    revoked_by     uuid REFERENCES nexora.app_user(id),
    revoke_reason  text,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT platform_license_scope_chk CHECK (scope IN ('organization','company','user','terminal','module')),
    CONSTRAINT platform_license_status_chk CHECK (status IN ('active','expired','revoked'))
);
CREATE INDEX platform_license_customer_idx ON nexora.platform_license (customer_id, status);

-- ===================== 9. Support (§37-38) =============================

CREATE TABLE nexora.support_ticket (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_ref      text        NOT NULL,                        -- « NXS-000123 »
    customer_id     uuid        REFERENCES nexora.platform_customer(id),
    company_id      uuid        REFERENCES nexora.company(id),   -- entreprise concernée (§37)
    subscription_id uuid        REFERENCES nexora.subscription(id),
    subject         text        NOT NULL,
    priority        text        NOT NULL DEFAULT 'p3',           -- p1..p4 (§37)
    status          text        NOT NULL DEFAULT 'open',         -- open | pending | resolved | closed
    requester_email text,
    assigned_to     uuid REFERENCES nexora.app_user(id),
    escalated       boolean     NOT NULL DEFAULT false,          -- (§37)
    opened_by       uuid REFERENCES nexora.app_user(id),
    resolved_at     timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT support_ticket_ref_uk UNIQUE (ticket_ref),
    CONSTRAINT support_ticket_priority_chk CHECK (priority IN ('p1','p2','p3','p4')),
    CONSTRAINT support_ticket_status_chk CHECK (status IN ('open','pending','resolved','closed'))
);
CREATE INDEX support_ticket_customer_idx ON nexora.support_ticket (customer_id, status);

CREATE TABLE nexora.support_ticket_message (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_id    uuid        NOT NULL REFERENCES nexora.support_ticket(id) ON DELETE CASCADE,
    author_user_id uuid REFERENCES nexora.app_user(id),
    body         text        NOT NULL,
    is_internal  boolean     NOT NULL DEFAULT false,             -- note interne (§37)
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX support_ticket_message_idx ON nexora.support_ticket_message (ticket_id, created_at);

-- Support assisté : accès temporaire aux données d'un client, avec consentement (§38).
CREATE TABLE nexora.support_access_grant (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id   uuid        NOT NULL REFERENCES nexora.platform_customer(id) ON DELETE CASCADE,
    company_id    uuid        REFERENCES nexora.company(id),     -- périmètre limité (§38)
    staff_user_id uuid        NOT NULL REFERENCES nexora.app_user(id),
    reason        text        NOT NULL,
    scope         text        NOT NULL DEFAULT 'read',           -- read | support_write
    requested_by  uuid REFERENCES nexora.app_user(id),           -- l'admin client qui consent (§38)
    approved_at   timestamptz,
    expires_at    timestamptz NOT NULL,                          -- durée limitée (§38)
    revoked_at    timestamptz,
    revoked_by    uuid REFERENCES nexora.app_user(id),
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT support_access_scope_chk CHECK (scope IN ('read','support_write'))
);
CREATE INDEX support_access_grant_active_idx ON nexora.support_access_grant (customer_id)
    WHERE approved_at IS NOT NULL AND revoked_at IS NULL;

-- ===================== 10. Journal d'événements billing (§48, §51) =====
-- Séparé des écritures comptables : corrections / prorata / avoirs sans réécrire l'historique (§44).

CREATE TABLE nexora.billing_event (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id   uuid        REFERENCES nexora.platform_customer(id) ON DELETE SET NULL,
    subscription_id uuid      REFERENCES nexora.subscription(id) ON DELETE SET NULL,
    event_type    text        NOT NULL,   -- TrialStarted | SubscriptionUpgraded | PaymentFailed | … (§51)
    payload       jsonb       NOT NULL DEFAULT '{}'::jsonb,
    actor_user_id uuid REFERENCES nexora.app_user(id),
    occurred_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX billing_event_customer_idx ON nexora.billing_event (customer_id, occurred_at DESC);
CREATE INDEX billing_event_type_idx     ON nexora.billing_event (event_type, occurred_at DESC);

-- ===================== 11. Audit (§48) & updated_at ====================

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'platform_plan','platform_addon','platform_customer','platform_customer_company',
        'subscription','subscription_addon','platform_coupon','platform_invoice','platform_invoice_line',
        'platform_payment','platform_payment_method','platform_license','support_ticket',
        'support_access_grant'
    ] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 12. Rôles & permissions plateforme (§35-36) =====

INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('platform.view',                 'Plateforme — consultation (console SaaS)',            'platform', 'view'),
    ('platform.manage_customers',     'Plateforme — gérer les comptes clients',              'platform', 'manage_customers'),
    ('platform.manage_catalogue',     'Plateforme — gérer plans, add-ons et coupons',        'platform', 'manage_catalogue'),
    ('platform.manage_subscriptions', 'Plateforme — créer / upgrader / suspendre un abonnement', 'platform', 'manage_subscriptions'),
    ('platform.manage_billing',       'Plateforme — factures, avoirs, remboursements',       'platform', 'manage_billing'),
    ('platform.record_payment',       'Plateforme — enregistrer / confirmer un paiement',    'platform', 'record_payment'),
    ('platform.manage_licenses',      'Plateforme — émettre / révoquer des licences',        'platform', 'manage_licenses'),
    ('platform.support',              'Plateforme — tickets de support',                     'platform', 'support'),
    ('platform.support_access',       'Plateforme — accès assisté aux données client',       'platform', 'support_access'),
    ('platform.security',             'Plateforme — revue anti-fraude et blocages',          'platform', 'security'),
    ('platform.portal',               'Portail client — mon abonnement, mes factures, mon usage', 'platform', 'portal')
ON CONFLICT (code) DO NOTHING;

-- Rôles plateforme dédiés (§35) — séparés des rôles locataires ; séparation des responsabilités (§36).
INSERT INTO nexora.role (name, description, system_role) VALUES
    ('platform_admin',    'NEXORA — Super administrateur plateforme',          true),
    ('platform_billing',  'NEXORA — Administrateur facturation',               true),
    ('platform_support',  'NEXORA — Support',                                  true),
    ('platform_finance',  'NEXORA — Finance (facturation, métriques)',         true),
    ('platform_security', 'NEXORA — Sécurité plateforme',                      true),
    ('platform_operator', 'NEXORA — Opérateur technique (licences, usage)',    true),
    ('platform_readonly', 'NEXORA — Lecture seule plateforme',                 true)
ON CONFLICT DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v51(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

-- Le super administrateur (rôle historique §8, accès plateforme complet) reçoit tout `platform.%`.
SELECT pg_temp.grant_perms_v51('super_administrator', ARRAY['platform.%']);
SELECT pg_temp.grant_perms_v51('platform_admin',      ARRAY['platform.%']);

-- Billing gère la facturation et le catalogue, PAS le support ni la sécurité (§36).
SELECT pg_temp.grant_perms_v51('platform_billing', ARRAY[
    'platform.view','platform.manage_customers','platform.manage_catalogue',
    'platform.manage_subscriptions','platform.manage_billing','platform.record_payment','platform.manage_licenses']);

-- Support gère les tickets et l'accès assisté ; il ne touche PAS aux paiements (§36).
SELECT pg_temp.grant_perms_v51('platform_support', ARRAY[
    'platform.view','platform.support','platform.support_access','platform.manage_customers']);

-- Finance : facturation + métriques, sans support ni sécurité.
SELECT pg_temp.grant_perms_v51('platform_finance', ARRAY[
    'platform.view','platform.manage_billing','platform.record_payment']);

-- Sécurité : anti-fraude uniquement — aucun accès automatique aux données métier (§36).
SELECT pg_temp.grant_perms_v51('platform_security', ARRAY['platform.view','platform.security']);

-- Opérateur technique : licences et usage.
SELECT pg_temp.grant_perms_v51('platform_operator', ARRAY['platform.view','platform.manage_licenses']);

-- Lecture seule.
SELECT pg_temp.grant_perms_v51('platform_readonly', ARRAY['platform.view']);

-- Le portail client (self-service §45-46) est ouvert aux administrateurs d'une organisation cliente.
SELECT pg_temp.grant_perms_v51('owner_direction',     ARRAY['platform.portal']);
SELECT pg_temp.grant_perms_v51('group_administrator', ARRAY['platform.portal']);

SELECT nexora.attach_common_triggers();
