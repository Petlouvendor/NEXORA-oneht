-- =============================================================================
-- NEXORA — V089 — Terrain : assigné → en route → sur place → terminé → preuve → synchronisé
-- (finalisation produit, phase 12)
--
--  * Horodatage de chaque étape d'un arrêt de livraison, et qui a conclu (livré / échec / intervention).
--  * Géolocalisation « uniquement si prévue et autorisée » : paramètre d'entreprise, désactivé par défaut ;
--    le navigateur demande en plus l'autorisation de l'utilisateur.
--  * Les étapes terrain sont tracées dans le journal d'audit.
-- Idempotente.
-- =============================================================================
ALTER TABLE nexora.delivery_stop
    ADD COLUMN IF NOT EXISTS en_route_at  timestamptz,
    ADD COLUMN IF NOT EXISTS arrived_at   timestamptz,
    ADD COLUMN IF NOT EXISTS completed_by uuid REFERENCES nexora.app_user(id);

ALTER TABLE nexora.field_intervention
    ADD COLUMN IF NOT EXISTS completed_by uuid REFERENCES nexora.app_user(id);

INSERT INTO nexora.setting_definition (key, category, value_type, default_value, allowed_values, max_scope, is_sensitive, affects_future, description)
VALUES ('field_ops.capture_location', 'terrain', 'boolean', 'false', '[]', 'company', true, true,
        'Enregistrer la position GPS au moment de la preuve de livraison (avec l''accord de l''appareil)')
ON CONFLICT (key) DO NOTHING;

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['delivery_route', 'delivery_stop', 'field_intervention'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;
