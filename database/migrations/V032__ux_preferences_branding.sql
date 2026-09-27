-- =============================================================================
-- NEXORA — V032 — Préférences UX & personnalisation (Phase 13 §35-36, §40)
-- Deux surfaces de personnalisation, sans jamais dégrader lisibilité / accessibilité :
--   - user_preference   : propre à l'utilisateur (thème, densité, langue, favoris…)
--   - company_branding   : propre à l'entreprise (nom affiché, accent, format reçu…)
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Préférences utilisateur (§36) ====================
CREATE TABLE nexora.user_preference (
    user_id            uuid PRIMARY KEY REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    theme              text        NOT NULL DEFAULT 'system',
    density            text        NOT NULL DEFAULT 'comfortable',
    locale             text        NOT NULL DEFAULT 'fr-FR',
    reduced_motion     boolean     NOT NULL DEFAULT false,
    dashboard_mode     text        NOT NULL DEFAULT 'summary',
    favorites          jsonb       NOT NULL DEFAULT '[]'::jsonb,
    table_columns      jsonb       NOT NULL DEFAULT '{}'::jsonb,
    notification_prefs jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT user_preference_theme_chk   CHECK (theme IN ('system','light','dark')),
    CONSTRAINT user_preference_density_chk CHECK (density IN ('comfortable','compact')),
    CONSTRAINT user_preference_dash_chk    CHECK (dashboard_mode IN ('summary','detailed')),
    CONSTRAINT user_preference_favorites_chk CHECK (jsonb_typeof(favorites) = 'array')
);

-- ===================== 2. Personnalisation entreprise (§35) ================
CREATE TABLE nexora.company_branding (
    company_id       uuid PRIMARY KEY REFERENCES nexora.company(id) ON DELETE CASCADE,
    display_name     text,
    accent_color     text,                                    -- #RRGGBB ; NULL = accent NEXORA par défaut
    logo_url         text,
    receipt_format   text        NOT NULL DEFAULT 'standard',
    document_footer  text,
    date_format      text        NOT NULL DEFAULT 'dd/MM/yyyy',
    number_locale    text        NOT NULL DEFAULT 'fr-FR',
    default_locale   text        NOT NULL DEFAULT 'fr-FR',
    configured_by    uuid        REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT company_branding_receipt_chk CHECK (receipt_format IN ('standard','compact','detailed')),
    CONSTRAINT company_branding_accent_chk  CHECK (accent_color IS NULL OR accent_color ~ '^#[0-9A-Fa-f]{6}$')
);

-- ===================== 3. RLS ============================================
-- user_preference : propre à l'utilisateur.
ALTER TABLE nexora.user_preference ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.user_preference FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS user_preference_isolation ON nexora.user_preference;
CREATE POLICY user_preference_isolation ON nexora.user_preference
    USING (nexora.rls_bypass() OR user_id = nexora.current_user_id())
    WITH CHECK (nexora.rls_bypass() OR user_id = nexora.current_user_id());

-- company_branding : rattrapage entreprise (company_id NOT NULL).
DO $$
DECLARE
    r record;
    excluded text[] := ARRAY['audit_log','notification','schema_migration','sector_module_preset','fraud_rule'];
BEGIN
    FOR r IN
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        LEFT JOIN pg_tables pt ON pt.schemaname = 'nexora' AND pt.tablename = c.table_name
        WHERE c.table_schema = 'nexora' AND c.column_name = 'company_id' AND c.is_nullable = 'NO'
          AND t.table_type = 'BASE TABLE' AND c.table_name <> ALL (excluded) AND (pt.rowsecurity IS NOT TRUE)
    LOOP
        PERFORM nexora.enable_company_rls(format('nexora.%I', r.table_name)::regclass);
    END LOOP;
END;
$$;

-- Audit de la personnalisation d'entreprise (§20, §42).
DROP TRIGGER IF EXISTS trg_audit_company_branding ON nexora.company_branding;
CREATE TRIGGER trg_audit_company_branding
    AFTER INSERT OR UPDATE OR DELETE ON nexora.company_branding
    FOR EACH ROW EXECUTE FUNCTION nexora.audit_row();

SELECT nexora.attach_common_triggers();
