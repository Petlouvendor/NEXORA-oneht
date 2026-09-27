-- =============================================================================
-- NEXORA — V045 — Configuration & adaptabilité : profils sectoriels & transfert (Phase 32)
--
-- Le moteur de configuration existe déjà :
--   Phase 8 (V027) : `company_activity`, `company_module` (activer/désactiver §8-9),
--     `sector_module_preset` (modules recommandés par activité), `terminology_override` (§7),
--     `custom_field` (+ `visibility` jsonb pour les champs conditionnels §12) / `custom_field_value`
--   Phase 14 (V033) : `setting` / `setting_definition` (config par périmètre §7-8),
--     `role` personnalisé (§23), `workflow(_run)` + moteur de règles + `TestAsync` (§20-22, §51),
--     `config_version` (draft / active / superseded, snapshot + diff + approbation §44-45, §53)
--   Phase 9 : `ICompanyProvisioningService` (onboarding §3) ; Phase 22 : scopes d'accès (§23-25)
--   Phase 30 : `document_sequence` (numérotation §19) ; Phase 13 : `company_branding` (§37)
--
-- Cette migration ajoute les **profils sectoriels réutilisables** (§6, §49) et le **transfert
-- de configuration** (export / import / clonage §46-48) :
--   sector_profile   : bundle de config versionné (modules + champs + terminologie + réglages + numérotation)
--   config_transfer  : journal des opérations d'export / import / clonage / application de profil (§47, §53)
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Profil sectoriel (§6, §49) =================
CREATE TABLE nexora.sector_profile (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid REFERENCES nexora.company(id),   -- NULL = modèle système / marketplace interne
    key          text        NOT NULL,
    name         text        NOT NULL,
    sector_type  text        NOT NULL,
    version      integer     NOT NULL DEFAULT 1,
    status       text        NOT NULL DEFAULT 'published',
    is_system    boolean     NOT NULL DEFAULT false,
    description  text,
    definition   jsonb       NOT NULL DEFAULT '{}'::jsonb,   -- { modules[], customFields[], terminology[], settings[], numbering[] }
    created_by   uuid REFERENCES nexora.app_user(id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sector_profile_status_chk CHECK (status IN ('draft','published','archived')),
    CONSTRAINT sector_profile_key_uk UNIQUE NULLS NOT DISTINCT (company_id, key)
);
CREATE INDEX sector_profile_lookup_idx ON nexora.sector_profile (sector_type, status);

-- ===================== 2. Transfert de configuration (§46-48, §53) ===
CREATE TABLE nexora.config_transfer (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid        NOT NULL REFERENCES nexora.company(id),
    kind              text        NOT NULL,
    source_company_id uuid REFERENCES nexora.company(id),
    source_profile_key text,
    dry_run           boolean     NOT NULL DEFAULT false,
    config_version_id uuid,                              -- snapshot pris avant application (rollback §44)
    summary           jsonb       NOT NULL DEFAULT '{}'::jsonb,   -- { modulesApplied, fieldsCreated, terminologySet, settingsSet, warnings[] }
    created_by        uuid REFERENCES nexora.app_user(id),
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT config_transfer_kind_chk CHECK (kind IN ('export','import','clone','apply_profile')),
    CONSTRAINT config_transfer_company_id_uk UNIQUE (id, company_id)
);
CREATE INDEX config_transfer_company_idx ON nexora.config_transfer (company_id, created_at DESC);

-- ===================== 3. RLS (§24, §58) & audit (§53) ===============
-- sector_profile : les modèles système (company_id NULL) restent visibles de tous ;
-- les profils d'entreprise sont isolés.
ALTER TABLE nexora.sector_profile ENABLE ROW LEVEL SECURITY;
ALTER TABLE nexora.sector_profile FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS sector_profile_isolation ON nexora.sector_profile;
CREATE POLICY sector_profile_isolation ON nexora.sector_profile
    USING (nexora.rls_bypass() OR company_id IS NULL OR nexora.rls_allows_company(company_id))
    WITH CHECK (nexora.rls_bypass() OR nexora.rls_allows_company(company_id));

SELECT nexora.enable_company_rls('nexora.config_transfer'::regclass);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['sector_profile','config_transfer'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

-- ===================== 4. Modèles système (§6, §49) =================
INSERT INTO nexora.sector_profile (company_id, key, name, sector_type, is_system, description, definition) VALUES
(NULL, 'pharmacy', 'Profil Pharmacie', 'pharmacy', true,
 'Lots, expiration, FEFO, traçabilité — vocabulaire patient.',
 '{
   "modules": [{"key":"pharmacy","enabled":true},{"key":"retail","enabled":true},{"key":"loyalty","enabled":true}],
   "customFields": [
     {"entityType":"product","fieldKey":"molecule","label":"Molécule / DCI","dataType":"text"},
     {"entityType":"product","fieldKey":"prescription_only","label":"Sur ordonnance","dataType":"boolean"},
     {"entityType":"customer","fieldKey":"insurance_number","label":"N° assurance","dataType":"text"}
   ],
   "terminology": [
     {"termKey":"customer","locale":"fr","label":"Patient"},
     {"termKey":"sale","locale":"fr","label":"Délivrance"}
   ],
   "settings": [
     {"key":"stock.expiry_alert_days","value":60},
     {"key":"stock.block_expired_sales","value":true}
   ],
   "numbering": []
 }'::jsonb),
(NULL, 'restaurant', 'Profil Restaurant', 'restaurant', true,
 'Tables, services, cuisine, pourboire, partage d''addition.',
 '{
   "modules": [{"key":"restaurant","enabled":true},{"key":"delivery","enabled":true}],
   "customFields": [
     {"entityType":"sale","fieldKey":"table_number","label":"Table","dataType":"text"},
     {"entityType":"sale","fieldKey":"covers","label":"Couverts","dataType":"number"},
     {"entityType":"sale","fieldKey":"service_period","label":"Service","dataType":"list","options":["midi","soir"]}
   ],
   "terminology": [
     {"termKey":"sale","locale":"fr","label":"Addition"},
     {"termKey":"customer","locale":"fr","label":"Table"}
   ],
   "settings": [],
   "numbering": []
 }'::jsonb),
(NULL, 'wholesale', 'Profil Grossiste', 'wholesaler', true,
 'Tarifs quantité, conditionnements, palettes, clients professionnels, crédit.',
 '{
   "modules": [{"key":"wholesale","enabled":true},{"key":"delivery","enabled":true}],
   "customFields": [
     {"entityType":"product","fieldKey":"carton_qty","label":"Quantité / carton","dataType":"number"},
     {"entityType":"product","fieldKey":"pallet_qty","label":"Quantité / palette","dataType":"number"},
     {"entityType":"customer","fieldKey":"tax_id_pro","label":"Identifiant professionnel","dataType":"text"},
     {"entityType":"customer","fieldKey":"is_professional","label":"Client professionnel","dataType":"boolean"}
   ],
   "terminology": [{"termKey":"customer","locale":"fr","label":"Client pro"}],
   "settings": [],
   "numbering": []
 }'::jsonb),
(NULL, 'retail', 'Profil Commerce de détail', 'boutique', true,
 'POS simple, fidélité, promotions — configuration minimale.',
 '{
   "modules": [{"key":"retail","enabled":true},{"key":"loyalty","enabled":true}],
   "customFields": [],
   "terminology": [],
   "settings": [],
   "numbering": []
 }'::jsonb),
(NULL, 'services', 'Profil Services', 'services', true,
 'Catalogue de prestations, projets, prestataires.',
 '{
   "modules": [{"key":"services","enabled":true},{"key":"appointments","enabled":true}],
   "customFields": [
     {"entityType":"service","fieldKey":"billing_unit","label":"Unité de facturation","dataType":"list","options":["heure","jour","forfait"]},
     {"entityType":"customer","fieldKey":"project_code","label":"Code projet","dataType":"text"}
   ],
   "terminology": [{"termKey":"sale","locale":"fr","label":"Prestation"}],
   "settings": [],
   "numbering": []
 }'::jsonb)
ON CONFLICT ON CONSTRAINT sector_profile_key_uk DO NOTHING;

-- ===================== 5. Permissions (§8, §23, §53) ================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('config.apply_profile', 'Configuration — appliquer un profil sectoriel', 'config', 'apply_profile'),
    ('config.transfer',      'Configuration — exporter / importer / cloner',  'config', 'transfer'),
    ('config.validate',      'Configuration — exécuter les garde-fous',        'config', 'validate')
ON CONFLICT (code) DO NOTHING;

CREATE OR REPLACE FUNCTION pg_temp.grant_perms_v45(p_role text, p_patterns text[])
RETURNS void LANGUAGE sql AS $$
    INSERT INTO nexora.role_permission (role_id, permission_id)
    SELECT r.id, p.id FROM nexora.role r
    JOIN nexora.permission p ON p.code LIKE ANY (p_patterns)
    WHERE r.name = p_role AND r.system_role
    ON CONFLICT DO NOTHING;
$$;

SELECT pg_temp.grant_perms_v45('super_administrator', ARRAY['config.%']);
SELECT pg_temp.grant_perms_v45('group_administrator', ARRAY['config.%']);
SELECT pg_temp.grant_perms_v45('owner_direction',     ARRAY['config.%']);

SELECT nexora.attach_common_triggers();
