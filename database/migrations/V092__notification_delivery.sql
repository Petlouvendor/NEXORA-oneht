-- =============================================================================
-- NEXORA — V092 — Notifications : fournisseurs, statut, relances, journal (finalisation produit, phase 16)
--
--  * Statut « not_configured » : un message destiné à un canal sans fournisseur configuré n'est ni
--    « envoyé » (le canal webhook l'annonçait) ni une panne à escalader (chaque message partait en
--    dead-letter avec une alerte administrateur). Il attend que le fournisseur soit configuré.
--  * Fournisseur utilisé et heure de la dernière tentative sur chaque envoi.
--  * Journal des tentatives : chaque essai (fournisseur, résultat, erreur, durée) est conservé.
--  * Dernier test de chaque canal (CONNECTED, AUTHENTICATION_FAILED, TIMEOUT, UNAVAILABLE, NOT_CONFIGURED).
--  * Permission « notifications.manage_delivery » : fournisseurs, journal, relances.
-- Idempotente.
-- =============================================================================
ALTER TABLE nexora.notification_outbox DROP CONSTRAINT IF EXISTS notification_outbox_status_chk;
ALTER TABLE nexora.notification_outbox ADD CONSTRAINT notification_outbox_status_chk
    CHECK (status IN ('pending','sending','sent','failed','dead','not_configured'));

ALTER TABLE nexora.notification_outbox
    ADD COLUMN IF NOT EXISTS provider text,
    ADD COLUMN IF NOT EXISTS last_attempt_at timestamptz;

CREATE TABLE IF NOT EXISTS nexora.notification_delivery_log (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    outbox_id    uuid NOT NULL REFERENCES nexora.notification_outbox(id) ON DELETE CASCADE,
    company_id   uuid REFERENCES nexora.company(id),
    attempt      smallint NOT NULL,
    status       text NOT NULL CHECK (status IN ('sent','failed','dead','not_configured')),
    provider     text,
    error        text,
    duration_ms  integer,
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS notification_delivery_log_outbox_ix ON nexora.notification_delivery_log (outbox_id, attempt);

SELECT nexora.enable_company_rls('nexora.notification_delivery_log');

-- Fournisseurs configurés au niveau de l'installation : un test par canal.
CREATE TABLE IF NOT EXISTS nexora.notification_channel_test (
    channel     text PRIMARY KEY CHECK (channel IN ('email','sms','whatsapp','push','webhook')),
    provider    text,
    status      text NOT NULL CHECK (status IN ('CONNECTED','AUTHENTICATION_FAILED','TIMEOUT','UNAVAILABLE','NOT_CONFIGURED')),
    message     text NOT NULL,
    latency_ms  integer,
    tested_at   timestamptz NOT NULL DEFAULT now(),
    tested_by   uuid REFERENCES nexora.app_user(id)
);

INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('notifications.manage_delivery', 'Notifications — fournisseurs d''envoi, journal des envois et relances', 'notifications', 'manage_delivery')
ON CONFLICT (code) DO NOTHING;

INSERT INTO nexora.role_permission (role_id, permission_id)
SELECT rp.role_id, p.id
FROM nexora.role_permission rp
JOIN nexora.permission src ON src.id = rp.permission_id AND src.code = 'notifications.manage_templates'
CROSS JOIN nexora.permission p
WHERE p.code = 'notifications.manage_delivery'
ON CONFLICT DO NOTHING;

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['message_template', 'notification_channel_test'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;
