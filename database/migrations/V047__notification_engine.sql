-- =============================================================================
-- NEXORA — V047 — Moteur de notifications & communication (Phase 34)
--
-- Le socle événementiel / automatisation existe déjà et reste VALIDÉ par cette phase :
--   Phase 7/31 : `alert_rule` + `IAlertCenter` (alertes évaluées à la volée §18-21)
--   Phase 11 (V016) : `notification` (centre in-app, RLS `user_id = current_user_id()`),
--     `approval_policy` / `approval_request` / `approval_action` (double approbation §36)
--   Phase 12 (V031) : connecteurs, `payment_intent`, **webhooks sortants signés + retry**
--     (`IOutboundWebhookService.EmitAsync` / `DeliverDueAsync` — §28) + entrants HMAC idempotents
--   Phase 14 (V033) : `workflow` / `workflow_run` + moteur de règles + `.TestAsync` (§11-14),
--     `automation` / `automation_run` (relances / alertes planifiées §7, §15-18, §41),
--     `dunning_policy` (relances clients à niveaux §16)
--   Phase 16 : `SchedulerRunner` — planificateur de fond idempotent (§7, §29, §32)
--   Phase 13 (V032) : `user_preference` (préférences UX ; `notification_prefs` jsonb libre)
--
-- Les automatisations écrivaient déjà `nexora.notification` par INSERT direct, mais sans
-- **préférences**, sans **routage multi-canal**, sans **templates** ni **file de remise fiable**.
-- Cette phase ajoute le moteur de distribution (§3-5, §22-23, §28-31, §45) :
--   notification            += category / priority / pinned_at / archived_at / expires_at /
--                              dedup_key / source                              (centre §4)
--   notification_preference : routage par catégorie & entreprise, priorité mini, heures calmes (§5)
--   message_template        : gabarits versionnés par canal & langue, variables déclarées (§22-23)
--   notification_outbox     : file de remise des canaux externes — retry, backoff, dead-letter,
--                              idempotence par (dedup_key, channel)            (§28-31)
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Centre de notifications (§4) =====================
ALTER TABLE nexora.notification
    ADD COLUMN IF NOT EXISTS category    text,
    ADD COLUMN IF NOT EXISTS priority    smallint    NOT NULL DEFAULT 3,   -- 1 = la plus haute … 5
    ADD COLUMN IF NOT EXISTS pinned_at   timestamptz,
    ADD COLUMN IF NOT EXISTS archived_at timestamptz,
    ADD COLUMN IF NOT EXISTS expires_at  timestamptz,
    ADD COLUMN IF NOT EXISTS dedup_key   text,
    ADD COLUMN IF NOT EXISTS source      text        NOT NULL DEFAULT 'system';

ALTER TABLE nexora.notification DROP CONSTRAINT IF EXISTS notification_priority_chk;
ALTER TABLE nexora.notification ADD  CONSTRAINT notification_priority_chk CHECK (priority BETWEEN 1 AND 5);
ALTER TABLE nexora.notification DROP CONSTRAINT IF EXISTS notification_source_chk;
ALTER TABLE nexora.notification ADD  CONSTRAINT notification_source_chk
    CHECK (source IN ('system','automation','workflow','alert','security','manual'));

-- Anti-duplication (§30) : une clé de dédup ne produit qu'une notification par destinataire.
CREATE UNIQUE INDEX IF NOT EXISTS notification_dedup_uk
    ON nexora.notification (user_id, dedup_key) WHERE dedup_key IS NOT NULL;
CREATE INDEX IF NOT EXISTS notification_user_pinned_idx
    ON nexora.notification (user_id) WHERE pinned_at IS NOT NULL AND archived_at IS NULL;

-- ===================== 2. Préférences de notification (§5) =================
CREATE TABLE nexora.notification_preference (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      uuid        NOT NULL REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    company_id   uuid REFERENCES nexora.company(id),   -- NULL = toutes les entreprises
    category     text,                                 -- NULL = toutes les catégories (préférence par défaut)
    in_app       boolean     NOT NULL DEFAULT true,
    email        boolean     NOT NULL DEFAULT false,
    sms          boolean     NOT NULL DEFAULT false,
    webhook      boolean     NOT NULL DEFAULT false,
    min_priority smallint    NOT NULL DEFAULT 5,        -- ne notifie que si priority <= min_priority
    digest       text        NOT NULL DEFAULT 'instant',
    quiet_from   time,                                  -- heures calmes : diffère les canaux externes
    quiet_to     time,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT notification_preference_digest_chk CHECK (digest IN ('instant','daily','weekly','off')),
    CONSTRAINT notification_preference_priority_chk CHECK (min_priority BETWEEN 1 AND 5),
    CONSTRAINT notification_preference_uk UNIQUE NULLS NOT DISTINCT (user_id, company_id, category)
);
CREATE INDEX notification_preference_user_idx ON nexora.notification_preference (user_id);

-- ===================== 3. Gabarits de message (§22-23) ====================
CREATE TABLE nexora.message_template (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid REFERENCES nexora.company(id),   -- NULL = gabarit système
    key          text        NOT NULL,
    channel      text        NOT NULL,
    locale       text        NOT NULL DEFAULT 'fr',
    subject      text        NOT NULL DEFAULT '',
    body         text        NOT NULL,
    description  text,
    variables    jsonb       NOT NULL DEFAULT '[]'::jsonb,   -- liste blanche déclarative des {{variables}}
    version      integer     NOT NULL DEFAULT 1,
    status       text        NOT NULL DEFAULT 'published',
    created_by   uuid REFERENCES nexora.app_user(id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT message_template_channel_chk CHECK (channel IN ('in_app','email','sms','whatsapp','push','webhook')),
    CONSTRAINT message_template_status_chk  CHECK (status IN ('draft','published','archived')),
    CONSTRAINT message_template_key_uk UNIQUE NULLS NOT DISTINCT (company_id, key, channel, locale)
);
CREATE INDEX message_template_lookup_idx ON nexora.message_template (key, channel, locale);

-- ===================== 4. File de remise des canaux externes (§28-31) =====
CREATE TABLE nexora.notification_outbox (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid REFERENCES nexora.company(id),
    user_id         uuid REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    notification_id uuid REFERENCES nexora.notification(id) ON DELETE SET NULL,
    channel         text        NOT NULL,
    category        text,
    priority        smallint    NOT NULL DEFAULT 3,
    subject         text        NOT NULL DEFAULT '',
    body            text        NOT NULL DEFAULT '',
    address         text,                                  -- email / n° / URL — résolu à l'émission
    status          text        NOT NULL DEFAULT 'pending',
    attempts        smallint    NOT NULL DEFAULT 0,
    max_attempts    smallint    NOT NULL DEFAULT 5,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_error      text,
    dedup_key       text,
    correlation_id  uuid        NOT NULL DEFAULT gen_random_uuid(),
    sent_at         timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT notification_outbox_channel_chk CHECK (channel IN ('email','sms','whatsapp','push','webhook')),
    CONSTRAINT notification_outbox_status_chk  CHECK (status IN ('pending','sending','sent','failed','dead')),
    CONSTRAINT notification_outbox_dedup_uk UNIQUE NULLS NOT DISTINCT (dedup_key, channel)
);
CREATE INDEX notification_outbox_due_idx ON nexora.notification_outbox (next_attempt_at)
    WHERE status IN ('pending','failed');

-- ===================== 5. RLS (§34, §37) & audit (§43) ====================
-- notification_preference : propre à l'utilisateur (comme notification / user_session).
ALTER TABLE nexora.notification_preference ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.notification_preference FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS notification_preference_isolation ON nexora.notification_preference;
CREATE POLICY notification_preference_isolation ON nexora.notification_preference
    USING (nexora.rls_bypass() OR user_id = nexora.current_user_id())
    WITH CHECK (nexora.rls_bypass() OR user_id = nexora.current_user_id());

-- notification_outbox : borné à l'entreprise (le planificateur agit en portée système).
SELECT nexora.enable_company_rls('nexora.notification_outbox'::regclass);

-- message_template : les gabarits système (company_id NULL) sont visibles de tous ;
-- les gabarits d'entreprise sont isolés.
ALTER TABLE nexora.message_template ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.message_template FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS message_template_isolation ON nexora.message_template;
CREATE POLICY message_template_isolation ON nexora.message_template
    USING (nexora.rls_bypass() OR company_id IS NULL OR nexora.rls_allows_company(company_id))
    WITH CHECK (nexora.rls_bypass() OR nexora.rls_allows_company(company_id));

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['notification_preference','message_template','notification_outbox'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 6. Permission (§34) ================================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('notifications.manage_templates', 'Notifications — gérer les gabarits de message', 'notifications', 'manage_templates')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v47(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;
SELECT pg_temp.grant_perms_v47('super_administrator', ARRAY['notifications.%']);
SELECT pg_temp.grant_perms_v47('group_administrator', ARRAY['notifications.%']);
SELECT pg_temp.grant_perms_v47('owner_direction',     ARRAY['notifications.%']);

-- ===================== 7. Gabarits système (§22-23) ======================
INSERT INTO nexora.message_template (company_id, key, channel, locale, subject, body, description, variables) VALUES
(NULL, 'dunning.reminder', 'in_app', 'fr',
 'Relance niveau {{level}} — {{customer.name}}',
 '{{reminder.title}} : solde {{invoice.balance}} en retard de {{invoice.days_late}} j.',
 'Relance client — notification interne',
 '["level","customer.name","reminder.title","invoice.balance","invoice.days_late"]'::jsonb),
(NULL, 'dunning.reminder', 'email', 'fr',
 'Rappel de paiement — {{company.name}}',
 'Bonjour {{customer.name}},\n\nVotre solde de {{invoice.balance}} est échu depuis {{invoice.days_late}} jours.\nMerci de régulariser dès que possible.\n\n{{company.name}}',
 'Relance client — email',
 '["customer.name","company.name","invoice.balance","invoice.days_late"]'::jsonb),
(NULL, 'stock.low', 'in_app', 'fr',
 'Stock bas — {{product.name}}',
 '{{stock.on_hand}} en stock, point de commande {{stock.reorder_point}}.',
 'Alerte stock bas — notification interne',
 '["product.name","stock.on_hand","stock.reorder_point"]'::jsonb),
(NULL, 'stock.expiring', 'in_app', 'fr',
 'Lot proche péremption — {{lot.number}}',
 'Le lot {{lot.number}} expire le {{lot.expiration_date}}.',
 'Alerte péremption — notification interne',
 '["lot.number","lot.expiration_date"]'::jsonb),
(NULL, 'notification.delivery_failed', 'in_app', 'fr',
 'Notification non délivrée',
 'Une notification « {{original.category}} » vers {{channel}} a échoué après {{attempts}} tentatives.',
 'Échec de remise — alerte administrateur (§31)',
 '["original.category","channel","attempts"]'::jsonb)
ON CONFLICT ON CONSTRAINT message_template_key_uk DO NOTHING;

SELECT nexora.attach_common_triggers();
