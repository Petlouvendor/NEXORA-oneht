-- =============================================================================
-- NEXORA ONE — V057 — Mobile, terrain & multi-appareils (Phase 44)
--
-- Le socle mobile/offline existe déjà et reste VALIDÉ par cette phase :
--   Phase 10 : `sync_device`/`sync_operation`/`sync_conflict`/`sync_batch`/`offline_policy`
--              (V017/V029) — file idempotente par `operation_uuid`, conflits PERSISTÉS et
--              résolubles (jamais un écrasement silencieux, §29), politique offline par
--              entreprise. `ISyncService.ProcessAsync` ne rejouait jusqu'ici QUE les ventes
--              (`entity_type = 'sale'`) — cette phase étend le même moteur, sans doublon,
--              aux opérations terrain (livraison, intervention).
--   Phase 25 : `OrderService.CompleteOrderAsync` — exécution d'une commande en vente réelle
--              (stock + encaissement + comptabilité) ; réutilisée par la livraison plutôt que
--              dupliquée (§52 « statut commande » découle du même appel).
--   Phase 34 : `notification_outbox.channel` / `message_template.channel` autorisent déjà la
--              valeur `'push'` depuis le V047 — jamais implémentée : cette phase ajoute le
--              canal, pas la colonne.
--
-- Cette migration ajoute ce qui n'existait nulle part :
--   `attachment`         : primitive MINIMALE photo/signature (aucun stockage de fichier
--                          n'existait avant cette phase) — Phase 45 (GED) la généralisera ;
--                          délibérément gardée simple ici (un blob + son hash), pas de
--                          versioning/recherche/OCR (hors périmètre de cette phase).
--   `delivery_route`/`delivery_stop` : tournées de livraison (§12, §52) — premier usage de
--                          coordonnées GPS dans tout le schéma (latitude/longitude).
--   `field_intervention` : interventions techniciens (§13) — aucun concept de « fiche
--                          d'intervention » n'existait (le `support_ticket` de la Phase 38 est
--                          le support NEXORA↔abonné, sans rapport avec le technicien terrain
--                          d'une entreprise cliente).
--   `crash_report`        : diagnostics minimisés (§46).
--   `sync_device` +       : `user_id` (un mobile est personnel, contrairement à un poste POS
--     `push_token`/         partagé), `push_token`/`push_provider` pour le nouveau canal push.
--     `push_provider`
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Appareil mobile : lien utilisateur + jeton push (§20, §32) ====

ALTER TABLE nexora.sync_device
    ADD COLUMN IF NOT EXISTS user_id       uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS push_token    text,
    ADD COLUMN IF NOT EXISTS push_provider text;
ALTER TABLE nexora.sync_device DROP CONSTRAINT IF EXISTS sync_device_push_provider_chk;
ALTER TABLE nexora.sync_device ADD CONSTRAINT sync_device_push_provider_chk
    CHECK (push_provider IS NULL OR push_provider IN ('fcm', 'apns', 'webpush'));

-- ===================== 2. Pièce jointe minimale — photo / signature (§22-23) =============
-- Primitive volontairement simple : un blob + son empreinte. Pas de versioning ni de
-- recherche — Phase 45 (GED) généralisera si besoin. `reference_type`/`reference_id` suit le
-- même principe polymorphe que `notification`/`task` (Phase 34/43).

CREATE TABLE nexora.attachment (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    kind           text        NOT NULL,                  -- photo | signature
    reference_type text,                                  -- delivery_stop | field_intervention
    reference_id   uuid,
    content        bytea       NOT NULL,
    content_type   text        NOT NULL DEFAULT 'image/png',
    size_bytes     integer     NOT NULL,
    sha256_hash    text        NOT NULL,
    captured_by    uuid        REFERENCES nexora.app_user(id),
    captured_at    timestamptz NOT NULL DEFAULT now(),
    created_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT attachment_kind_chk CHECK (kind IN ('photo', 'signature')),
    CONSTRAINT attachment_size_chk CHECK (size_bytes > 0 AND size_bytes <= 5242880)   -- 5 Mo, capture mobile seulement
);
CREATE INDEX attachment_reference_idx ON nexora.attachment (reference_type, reference_id);

-- ===================== 3. Tournées de livraison (§12, §52) ================================
-- Premier usage de coordonnées géographiques dans le schéma NEXORA — volontaire et justifié
-- (§24 : jamais un suivi permanent, seulement le point de livraison au moment de la preuve).

CREATE TABLE nexora.delivery_route (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    driver_user_id  uuid        REFERENCES nexora.app_user(id),
    route_date      date        NOT NULL DEFAULT CURRENT_DATE,
    vehicle         text,
    status          text        NOT NULL DEFAULT 'planned',
    started_at      timestamptz,
    completed_at    timestamptz,
    notes           text        NOT NULL DEFAULT '',
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT delivery_route_status_chk CHECK (status IN ('planned', 'in_progress', 'completed', 'cancelled')),
    CONSTRAINT delivery_route_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX delivery_route_company_date_idx ON nexora.delivery_route (company_id, route_date DESC);
CREATE INDEX delivery_route_driver_idx ON nexora.delivery_route (driver_user_id, status);

CREATE TABLE nexora.delivery_stop (
    id                          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id                  uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    delivery_route_id           uuid        NOT NULL,
    sequence_no                 integer     NOT NULL DEFAULT 1,
    sales_order_id              uuid,                      -- NULL = livraison sans commande à solder (§12)
    customer_id                 uuid,
    address                     text        NOT NULL DEFAULT '',
    latitude                    numeric(9,6),
    longitude                   numeric(9,6),
    contact_phone               text,
    status                      text        NOT NULL DEFAULT 'pending',
    signature_attachment_id     uuid REFERENCES nexora.attachment(id),
    photo_attachment_id         uuid REFERENCES nexora.attachment(id),
    payment_financial_account_id uuid,
    amount_collected            nexora.money_amount,
    delivered_at                timestamptz,
    failure_reason              text,
    notes                       text        NOT NULL DEFAULT '',
    created_at                  timestamptz NOT NULL DEFAULT now(),
    updated_at                  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT delivery_stop_status_chk CHECK (status IN ('pending', 'en_route', 'arrived', 'delivered', 'failed', 'returned')),
    CONSTRAINT delivery_stop_amount_chk CHECK (amount_collected IS NULL OR amount_collected >= 0),
    CONSTRAINT delivery_stop_route_fk FOREIGN KEY (delivery_route_id, company_id)
        REFERENCES nexora.delivery_route (id, company_id) ON DELETE CASCADE,
    CONSTRAINT delivery_stop_order_fk FOREIGN KEY (sales_order_id, company_id)
        REFERENCES nexora.sales_order (id, company_id),
    CONSTRAINT delivery_stop_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id),
    CONSTRAINT delivery_stop_account_fk FOREIGN KEY (payment_financial_account_id, company_id)
        REFERENCES nexora.financial_account (id, company_id)
);
CREATE INDEX delivery_stop_route_idx ON nexora.delivery_stop (delivery_route_id, sequence_no);
CREATE INDEX delivery_stop_status_idx ON nexora.delivery_stop (company_id, status);

-- ===================== 4. Interventions techniciens (§13) ==================================

CREATE TABLE nexora.field_intervention (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id              uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    technician_user_id      uuid        REFERENCES nexora.app_user(id),
    customer_id             uuid,
    equipment_description   text        NOT NULL DEFAULT '',
    checklist               jsonb       NOT NULL DEFAULT '[]'::jsonb,   -- [{label, done}]
    parts_used              jsonb       NOT NULL DEFAULT '[]'::jsonb,   -- [{productId, quantity}]
    scheduled_at            timestamptz,
    started_at              timestamptz,
    completed_at            timestamptz,
    time_spent_minutes      integer,
    status                  text        NOT NULL DEFAULT 'scheduled',
    signature_attachment_id uuid REFERENCES nexora.attachment(id),
    report_text             text        NOT NULL DEFAULT '',
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT field_intervention_status_chk CHECK (status IN ('scheduled', 'in_progress', 'completed', 'cancelled')),
    CONSTRAINT field_intervention_time_chk CHECK (time_spent_minutes IS NULL OR time_spent_minutes >= 0),
    CONSTRAINT field_intervention_customer_fk FOREIGN KEY (customer_id, company_id)
        REFERENCES nexora.customer (id, company_id)
);
CREATE INDEX field_intervention_company_status_idx ON nexora.field_intervention (company_id, status);
CREATE INDEX field_intervention_technician_idx ON nexora.field_intervention (technician_user_id, status);

-- ===================== 5. Diagnostics minimisés (§46) =======================================

CREATE TABLE nexora.crash_report (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    device_id      uuid REFERENCES nexora.sync_device(id),
    app_version    text        NOT NULL DEFAULT '',
    os             text        NOT NULL DEFAULT '',
    os_version     text        NOT NULL DEFAULT '',
    correlation_id text,
    message        text        NOT NULL DEFAULT '',
    stack_excerpt  text        NOT NULL DEFAULT '',
    occurred_at    timestamptz NOT NULL DEFAULT now(),
    created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX crash_report_company_idx ON nexora.crash_report (company_id, occurred_at DESC);

-- ===================== 6. RLS + audit (§30, §40) ============================================

SELECT nexora.enable_company_rls('nexora.attachment'::regclass);
SELECT nexora.enable_company_rls('nexora.delivery_route'::regclass);
SELECT nexora.enable_company_rls('nexora.delivery_stop'::regclass);
SELECT nexora.enable_company_rls('nexora.field_intervention'::regclass);
SELECT nexora.enable_company_rls('nexora.crash_report'::regclass);

DO $$
DECLARE t text;
BEGIN
    -- attachment et crash_report ne sont pas mis à jour après coup (§ journal d'ajout seul,
    -- comme assistant_query V056) — seuls delivery_route/delivery_stop/field_intervention le sont.
    FOREACH t IN ARRAY ARRAY['delivery_route','delivery_stop','field_intervention'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 7. Permissions (§48 périmètre) =======================================

INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('field_ops.view',   'Terrain — consulter tournées, interventions, diagnostics', 'field_ops', 'view'),
    ('field_ops.manage', 'Terrain — planifier/compléter livraisons et interventions', 'field_ops', 'manage')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v57(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;
SELECT pg_temp.grant_perms_v57('super_administrator', ARRAY['field_ops.%']);
SELECT pg_temp.grant_perms_v57('group_administrator', ARRAY['field_ops.%']);
SELECT pg_temp.grant_perms_v57('owner_direction',     ARRAY['field_ops.%']);
SELECT pg_temp.grant_perms_v57('manager',             ARRAY['field_ops.%']);
-- Aucun rôle "livreur"/"technicien" ne modélise encore un profil dédié (§5 — constat, pas
-- construit cette phase) : le rôle le plus proche opérationnellement reçoit le droit d'agir sur
-- SES tournées/interventions (filtrage explicite service, comme les autres modules).
SELECT pg_temp.grant_perms_v57('salesperson',   ARRAY['field_ops.view','field_ops.manage']);
SELECT pg_temp.grant_perms_v57('stock_manager', ARRAY['field_ops.view','field_ops.manage']);
SELECT pg_temp.grant_perms_v57('cashier',       ARRAY['field_ops.view']);

SELECT nexora.attach_common_triggers();
