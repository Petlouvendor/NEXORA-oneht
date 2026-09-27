-- =============================================================================
-- NEXORA — V048 — Plateforme ouverte : clés d'API pour les intégrations serveur (Phase 35)
--
-- Le socle d'intégration existe déjà et reste VALIDÉ par cette phase :
--   Phase 12 (V031) : `integration_connector` (couche connecteurs remplaçable §2-5),
--     `payment_intent` + adaptateur simulé → paiement réel (§7-9), `webhook_event`
--     (entrants HMAC + protection replay + idempotence §16), `outbound_webhook` /
--     `outbound_webhook_delivery` (sortants signés + retry + backoff + dead-letter §17),
--     rapprochement bancaire + import de relevé (§6), centre d'intégration + santé (§4-5, §42)
--   Phase 10 (V017/V029) : moteur de synchronisation offline, curseurs, reprise après erreur (§18)
--   Phase 34 (V047) : `notification_outbox` + distributeur (retry / dead-letter §46), gabarits
--   Phase 3 : API versionnée `/api/v1`, deny-by-default, JWT (auth humaine — Phase 33 MFA)
--   Phase 7 : `CsvExportService` + `export_log` (§22)
--
-- Cette migration ajoute l'**authentification machine-à-machine** décrite aux §13-14, §43-44 :
--   api_key : clé d'API par entreprise, à périmètre (scopes) borné, expirable, rotable, révocable ;
--             le secret n'est stocké que haché (SHA-256) et n'est **jamais** ré-affiché (§14).
-- =============================================================================
SET search_path TO nexora, public;

CREATE TABLE nexora.api_key (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid        NOT NULL REFERENCES nexora.company(id),
    name                 text        NOT NULL,
    key_prefix           text        NOT NULL,                 -- visible : « nxk_live_ab12cd34 »
    key_hash             text        NOT NULL,                 -- SHA-256 du secret complet — jamais en clair (§44)
    environment          text        NOT NULL DEFAULT 'live',
    scopes               jsonb       NOT NULL DEFAULT '[]'::jsonb,  -- liste de codes de permission (§13)
    rate_limit_per_minute integer    NOT NULL DEFAULT 120,     -- quota (§13, §33)
    owner_user_id        uuid        NOT NULL REFERENCES nexora.app_user(id),  -- identité d'exécution (§13, §35)
    expires_at           timestamptz,
    last_used_at         timestamptz,
    last_used_ip         inet,
    revoked_at           timestamptz,
    revoked_by           uuid REFERENCES nexora.app_user(id),
    rotated_from         uuid REFERENCES nexora.api_key(id),   -- traçabilité de rotation (§14)
    created_by           uuid REFERENCES nexora.app_user(id),
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT api_key_env_chk CHECK (environment IN ('test','live')),
    CONSTRAINT api_key_rate_chk CHECK (rate_limit_per_minute BETWEEN 1 AND 100000),
    CONSTRAINT api_key_prefix_uk UNIQUE (key_prefix),
    CONSTRAINT api_key_hash_uk   UNIQUE (key_hash)
);
CREATE INDEX api_key_company_idx ON nexora.api_key (company_id, created_at DESC);
CREATE INDEX api_key_active_idx  ON nexora.api_key (key_prefix) WHERE revoked_at IS NULL;

-- RLS (§43, §48) — clé bornée à l'entreprise ; l'authentificateur agit en portée système.
SELECT nexora.enable_company_rls('nexora.api_key'::regclass);

DROP TRIGGER IF EXISTS trg_audit_api_key ON nexora.api_key;
CREATE TRIGGER trg_audit_api_key AFTER INSERT OR UPDATE OR DELETE ON nexora.api_key
    FOR EACH ROW EXECUTE FUNCTION nexora.audit_row();

-- Permission (§13-14, §43)
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('integrations.manage_api_keys', 'Intégrations — gérer les clés d''API', 'integrations', 'manage_api_keys')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v48(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;
SELECT pg_temp.grant_perms_v48('super_administrator', ARRAY['integrations.manage_api_keys']);
SELECT pg_temp.grant_perms_v48('group_administrator', ARRAY['integrations.manage_api_keys']);
SELECT pg_temp.grant_perms_v48('owner_direction',     ARRAY['integrations.manage_api_keys']);

SELECT nexora.attach_common_triggers();
