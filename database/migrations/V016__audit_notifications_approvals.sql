-- =============================================================================
-- NEXORA — V016 — Audit, notifications, alertes et workflows d'approbation
-- (§23, §24, §25, §26, §27 du modèle de données)
-- =============================================================================
SET search_path TO nexora, public;

-- --- Journal d'audit (§25) — immuable -------------------------
CREATE TABLE nexora.audit_log (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid,
    user_id      uuid,                       -- pas de FK : l'audit survit à la suppression d'un compte
    action       text        NOT NULL,
    entity_type  text        NOT NULL,
    entity_id    uuid,
    old_values   jsonb,
    new_values   jsonb,
    ip_address   inet,
    device_id    text,
    context      jsonb       NOT NULL DEFAULT '{}'::jsonb,
    occurred_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX audit_log_company_time_idx ON nexora.audit_log (company_id, occurred_at DESC);
CREATE INDEX audit_log_entity_idx       ON nexora.audit_log (entity_type, entity_id);
CREATE INDEX audit_log_user_idx         ON nexora.audit_log (user_id, occurred_at DESC);

-- --- Notifications (§26) -------------------------------------
CREATE TABLE nexora.notification (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id        uuid        NOT NULL REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    company_id     uuid        REFERENCES nexora.company(id),
    type           text        NOT NULL,
    severity       text        NOT NULL DEFAULT 'info',
    title          text        NOT NULL,
    message        text        NOT NULL DEFAULT '',
    reference_type text,
    reference_id   uuid,
    action_url     text,
    read_at        timestamptz,
    created_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT notification_severity_chk CHECK (severity IN ('info','success','warning','error','critical'))
);
CREATE INDEX notification_user_idx        ON nexora.notification (user_id, created_at DESC);
CREATE INDEX notification_user_unread_idx ON nexora.notification (user_id) WHERE read_at IS NULL;

-- --- Règles d'alerte (§26) ---------------------------------
CREATE TABLE nexora.alert_rule (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id    uuid        NOT NULL REFERENCES nexora.company(id),
    name          text        NOT NULL,
    rule_type     text        NOT NULL,
    threshold     numeric(20,6),
    configuration jsonb       NOT NULL DEFAULT '{}'::jsonb,
    scope_config  jsonb       NOT NULL DEFAULT '{}'::jsonb,
    status        text        NOT NULL DEFAULT 'active',
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT alert_rule_type_chk CHECK (rule_type IN (
        'low_stock','expiring_lot','overdue_receivable','overdue_payable','cash_variance',
        'budget_overrun','sales_drop','margin_drop','anomaly','custom')),
    CONSTRAINT alert_rule_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT alert_rule_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX alert_rule_company_idx ON nexora.alert_rule (company_id);

-- --- Politiques de seuils d'approbation (§23) --------------
CREATE TABLE nexora.approval_policy (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid        NOT NULL REFERENCES nexora.company(id),
    request_type     text        NOT NULL,
    establishment_id uuid,
    min_amount       nexora.money_amount NOT NULL DEFAULT 0,
    currency_id      uuid        REFERENCES nexora.currency(id),
    required_levels  smallint    NOT NULL DEFAULT 1,
    approver_role_id uuid        REFERENCES nexora.role(id),
    configuration    jsonb       NOT NULL DEFAULT '{}'::jsonb,
    status           text        NOT NULL DEFAULT 'active',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT approval_policy_request_type_chk CHECK (request_type IN (
        'expense','discount','refund','purchase','credit_sale','stock_adjustment',
        'price_change','void','period_close','other')),
    CONSTRAINT approval_policy_levels_chk CHECK (required_levels BETWEEN 1 AND 10),
    CONSTRAINT approval_policy_status_chk CHECK (status IN ('active','inactive')),
    CONSTRAINT approval_policy_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id)
);
CREATE INDEX approval_policy_company_idx ON nexora.approval_policy (company_id, request_type);

-- --- Demandes et actions d'approbation (§27) --------------
CREATE TABLE nexora.approval_request (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid        NOT NULL REFERENCES nexora.company(id),
    request_type    text        NOT NULL,
    reference_type  text,
    reference_id    uuid,
    requested_by    uuid        REFERENCES nexora.app_user(id),
    amount          nexora.money_amount,
    currency_id     uuid        REFERENCES nexora.currency(id),
    status          text        NOT NULL DEFAULT 'pending',
    current_level   smallint    NOT NULL DEFAULT 1,
    required_levels smallint    NOT NULL DEFAULT 1,
    requested_at    timestamptz NOT NULL DEFAULT now(),
    completed_at    timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT approval_request_type_chk CHECK (request_type IN (
        'expense','discount','refund','purchase','credit_sale','stock_adjustment',
        'price_change','void','period_close','other')),
    CONSTRAINT approval_request_status_chk CHECK (status IN (
        'pending','approved','rejected','cancelled','escalated')),
    CONSTRAINT approval_request_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX approval_request_company_status_idx ON nexora.approval_request (company_id, status);
CREATE INDEX approval_request_reference_idx      ON nexora.approval_request (reference_type, reference_id);

CREATE TABLE nexora.approval_action (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL,
    approval_request_id uuid        NOT NULL,
    approver_id         uuid        REFERENCES nexora.app_user(id),
    level               smallint    NOT NULL DEFAULT 1,
    action              text        NOT NULL,
    comment             text,
    created_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT approval_action_action_chk CHECK (action IN (
        'approve','reject','request_changes','delegate','comment')),
    CONSTRAINT approval_action_request_fk FOREIGN KEY (approval_request_id, company_id)
        REFERENCES nexora.approval_request (id, company_id) ON DELETE CASCADE
);
CREATE INDEX approval_action_request_idx ON nexora.approval_action (approval_request_id);

-- --- Lien différé : dépense → demande d'approbation --------
ALTER TABLE nexora.expense
    ADD CONSTRAINT expense_approval_request_fk FOREIGN KEY (approval_request_id, company_id)
        REFERENCES nexora.approval_request (id, company_id);

SELECT nexora.attach_common_triggers();
