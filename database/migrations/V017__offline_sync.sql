-- =============================================================================
-- NEXORA — V017 — Offline et synchronisation (§7 CDC, §28)
-- PENDING → SYNCING → SYNCED ↘ FAILED / CONFLICT
-- operation_uuid globalement unique → synchronisation idempotente.
-- =============================================================================
SET search_path TO nexora, public;

CREATE TABLE nexora.sync_device (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid        NOT NULL REFERENCES nexora.company(id),
    device_identifier text        NOT NULL,
    device_type       text        NOT NULL DEFAULT 'pos',
    name              text,
    pos_terminal_id   uuid,
    public_key        text,
    last_sync_at      timestamptz,
    last_seen_at      timestamptz,
    status            text        NOT NULL DEFAULT 'active',
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sync_device_type_chk   CHECK (device_type IN ('pos','mobile','desktop','server','other')),
    CONSTRAINT sync_device_status_chk CHECK (status IN ('active','suspended','revoked')),
    CONSTRAINT sync_device_company_identifier_uk UNIQUE (company_id, device_identifier),
    CONSTRAINT sync_device_company_id_uk         UNIQUE (id, company_id),
    CONSTRAINT sync_device_pos_terminal_fk FOREIGN KEY (pos_terminal_id, company_id)
        REFERENCES nexora.pos_terminal (id, company_id)
);
CREATE INDEX sync_device_company_idx ON nexora.sync_device (company_id);

CREATE TABLE nexora.sync_operation (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid        NOT NULL,
    device_id           uuid        NOT NULL,
    operation_uuid      uuid        NOT NULL,          -- §28 : identifiant unique global
    entity_type         text        NOT NULL,
    entity_id           uuid,
    operation_type      text        NOT NULL,
    payload             jsonb       NOT NULL,
    client_sequence     bigint,
    client_created_at   timestamptz,
    status              text        NOT NULL DEFAULT 'PENDING',
    attempts            integer     NOT NULL DEFAULT 0,
    error_code          text,
    error_detail        text,
    conflict_resolution text,
    synced_at           timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sync_operation_type_chk   CHECK (operation_type IN ('create','update','delete')),
    CONSTRAINT sync_operation_status_chk CHECK (status IN ('PENDING','SYNCING','SYNCED','FAILED','CONFLICT')),
    CONSTRAINT sync_operation_uuid_uk    UNIQUE (operation_uuid),
    CONSTRAINT sync_operation_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT sync_operation_device_fk FOREIGN KEY (device_id, company_id)
        REFERENCES nexora.sync_device (id, company_id)
);
CREATE INDEX sync_operation_company_status_idx ON nexora.sync_operation (company_id, status);
CREATE INDEX sync_operation_device_status_idx  ON nexora.sync_operation (device_id, status, client_sequence);
CREATE INDEX sync_operation_entity_idx         ON nexora.sync_operation (entity_type, entity_id);

CREATE TABLE nexora.sync_conflict (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid        NOT NULL,
    sync_operation_id uuid        NOT NULL,
    entity_type       text        NOT NULL,
    entity_id         uuid,
    server_version    jsonb,
    client_version    jsonb,
    resolution        text        NOT NULL DEFAULT 'pending',
    resolved_by       uuid        REFERENCES nexora.app_user(id),
    resolved_at       timestamptz,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sync_conflict_resolution_chk CHECK (resolution IN (
        'pending','server_wins','client_wins','merged','manual')),
    CONSTRAINT sync_conflict_operation_fk FOREIGN KEY (sync_operation_id, company_id)
        REFERENCES nexora.sync_operation (id, company_id) ON DELETE CASCADE
);
CREATE INDEX sync_conflict_company_idx ON nexora.sync_conflict (company_id, resolution);

SELECT nexora.attach_common_triggers();
