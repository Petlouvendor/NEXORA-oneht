-- =============================================================================
-- NEXORA ONE — V053 — UX, design system & accessibilité : personnalisation persistée (Phase 40)
--
-- Le design system est déjà VALIDÉ (Phase 13 : `theme.css` tokens light/dark/densité/motion,
-- `shared/ui`, `PreferencesContext`, palette Ctrl+K ; `user_preference` + `company_branding` V032 ;
-- Phase 19 : architecture frontend validée + Vitest). Cette phase ajoute la **couche de
-- personnalisation persistée** décrite aux §13, §21-22, §31-33, §37, §50, §57 :
--
--   user_preference += high_contrast / text_scale / timezone   (accessibilité §31-33, i18n §34)
--   dashboard_layout   : dispositions de tableau de bord par utilisateur / rôle (§13, §37)
--   saved_filter       : filtres de tableaux sauvegardés, privés ou partagés (§21-22, §37, §44)
--   ui_shortcut        : raccourcis clavier / actions rapides de l'utilisateur (§11, §20, §37-38)
--
-- La couleur d'accent d'entreprise reste bornée en luminance côté service (§6, §35 — jamais
-- dégrader l'accessibilité). Aucune couleur ne porte seule un état (§6, §31).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Préférences d'accessibilité (§31-33, §34) =======

ALTER TABLE nexora.user_preference
    ADD COLUMN IF NOT EXISTS high_contrast boolean       NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS text_scale    numeric(3,2)  NOT NULL DEFAULT 1.00,
    ADD COLUMN IF NOT EXISTS timezone      text          NOT NULL DEFAULT 'America/Port-au-Prince',
    ADD COLUMN IF NOT EXISTS shortcuts     jsonb         NOT NULL DEFAULT '{}'::jsonb;

ALTER TABLE nexora.user_preference DROP CONSTRAINT IF EXISTS user_preference_text_scale_chk;
ALTER TABLE nexora.user_preference
    ADD CONSTRAINT user_preference_text_scale_chk CHECK (text_scale BETWEEN 0.85 AND 1.50);

-- ===================== 2. Dispositions de tableau de bord (§13, §37) ======
-- user_id NULL = disposition par défaut d'entreprise (éventuellement ciblée par rôle).

CREATE TABLE nexora.dashboard_layout (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id  uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    user_id     uuid        REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    role        text,                                        -- ciblage rôle pour un défaut d'entreprise (§37)
    name        text        NOT NULL DEFAULT 'Mon tableau de bord',
    widgets     jsonb       NOT NULL DEFAULT '[]'::jsonb,     -- [{ key, x, y, w, h, config }] (§13)
    is_default  boolean     NOT NULL DEFAULT false,
    created_by  uuid        REFERENCES nexora.app_user(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT dashboard_layout_widgets_chk CHECK (jsonb_typeof(widgets) = 'array'),
    CONSTRAINT dashboard_layout_scope_chk   CHECK (user_id IS NOT NULL OR role IS NOT NULL OR is_default)
);
CREATE INDEX dashboard_layout_user_idx    ON nexora.dashboard_layout (user_id) WHERE user_id IS NOT NULL;
CREATE INDEX dashboard_layout_company_idx ON nexora.dashboard_layout (company_id) WHERE user_id IS NULL;
-- Une seule disposition « par défaut » par (entreprise, utilisateur) et par (entreprise, rôle).
CREATE UNIQUE INDEX dashboard_layout_user_default_idx ON nexora.dashboard_layout (company_id, user_id)
    WHERE user_id IS NOT NULL AND is_default;
CREATE UNIQUE INDEX dashboard_layout_role_default_idx ON nexora.dashboard_layout (company_id, role)
    WHERE user_id IS NULL AND role IS NOT NULL AND is_default;

-- ===================== 3. Filtres de tableaux sauvegardés (§21-22, §44) ===

CREATE TABLE nexora.saved_filter (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id  uuid        NOT NULL REFERENCES nexora.company(id) ON DELETE CASCADE,
    user_id     uuid        NOT NULL REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    view_key    text        NOT NULL,                         -- « sales.list », « documents.list »… (§22)
    name        text        NOT NULL,
    filter      jsonb       NOT NULL DEFAULT '{}'::jsonb,
    is_default  boolean     NOT NULL DEFAULT false,
    shared      boolean     NOT NULL DEFAULT false,           -- visible par les collègues de l'entreprise (§37)
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT saved_filter_uk UNIQUE (user_id, view_key, name)
);
CREATE INDEX saved_filter_lookup_idx ON nexora.saved_filter (company_id, view_key);
CREATE UNIQUE INDEX saved_filter_default_idx ON nexora.saved_filter (user_id, view_key) WHERE is_default;

-- ===================== 4. Raccourcis utilisateur (§11, §20, §37-38) ======

CREATE TABLE nexora.ui_shortcut (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     uuid        NOT NULL REFERENCES nexora.app_user(id) ON DELETE CASCADE,
    company_id  uuid        REFERENCES nexora.company(id) ON DELETE CASCADE,
    chord       text        NOT NULL,                         -- « g d », « n v »… (raccourci clavier)
    action      text        NOT NULL,                         -- route ou commande de la palette
    label       text        NOT NULL,
    sort_order  integer     NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ui_shortcut_uk UNIQUE (user_id, chord)
);
CREATE INDEX ui_shortcut_user_idx ON nexora.ui_shortcut (user_id, sort_order);

-- ===================== 5. RLS (§56 sécurité UX, scope entreprise) ========

-- dashboard_layout : sa propre disposition + les défauts d'entreprise (user_id NULL) de ses entreprises.
ALTER TABLE nexora.dashboard_layout ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.dashboard_layout FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS dashboard_layout_isolation ON nexora.dashboard_layout;
CREATE POLICY dashboard_layout_isolation ON nexora.dashboard_layout
    USING (nexora.rls_bypass()
        OR user_id = nexora.current_user_id()
        OR (user_id IS NULL AND nexora.rls_allows_company(company_id)))
    WITH CHECK (nexora.rls_bypass()
        OR user_id = nexora.current_user_id()
        OR (user_id IS NULL AND nexora.rls_allows_company(company_id)));

-- saved_filter : ses propres filtres + les filtres partagés de ses entreprises ; écriture = propriétaire.
ALTER TABLE nexora.saved_filter ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.saved_filter FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS saved_filter_isolation ON nexora.saved_filter;
CREATE POLICY saved_filter_isolation ON nexora.saved_filter
    USING (nexora.rls_bypass()
        OR user_id = nexora.current_user_id()
        OR (shared AND nexora.rls_allows_company(company_id)))
    WITH CHECK (nexora.rls_bypass() OR user_id = nexora.current_user_id());

-- ui_shortcut : propre à l'utilisateur.
ALTER TABLE nexora.ui_shortcut ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.ui_shortcut FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS ui_shortcut_isolation ON nexora.ui_shortcut;
CREATE POLICY ui_shortcut_isolation ON nexora.ui_shortcut
    USING (nexora.rls_bypass() OR user_id = nexora.current_user_id())
    WITH CHECK (nexora.rls_bypass() OR user_id = nexora.current_user_id());

-- ===================== 6. Audit (§56) ==================================

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['dashboard_layout','saved_filter','ui_shortcut'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

SELECT nexora.attach_common_triggers();
