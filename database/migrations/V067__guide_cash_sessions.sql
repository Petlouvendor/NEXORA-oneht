-- =============================================================================
-- NEXORA — V067 — Guide fonctionnel développeur, étape 4 : sessions de caisse
-- (docs/guide-fonctionnel-conformite.md, écarts GF-11, GF-12).
--
-- Session caisse : Open → Closing → Closed (§26). « Un écart important peut nécessiter validation
-- Manager. Une clôture finalisée ne doit pas être modifiable librement » (§13). Au-delà du seuil
-- pos.cash_variance_threshold, la clôture comptée reste « closing » (plus aucune vente) jusqu'à la
-- validation d'un superviseur distinct du caissier ; l'écriture d'écart n'est passée qu'à ce moment.
-- =============================================================================
SET search_path TO nexora, public;

ALTER TABLE nexora.cash_session ADD COLUMN IF NOT EXISTS counted_at            timestamptz;
ALTER TABLE nexora.cash_session ADD COLUMN IF NOT EXISTS close_validated_by    uuid REFERENCES nexora.app_user(id);
ALTER TABLE nexora.cash_session ADD COLUMN IF NOT EXISTS close_validated_at    timestamptz;
ALTER TABLE nexora.cash_session ADD COLUMN IF NOT EXISTS close_validation_note text;
ALTER TABLE nexora.cash_session DROP CONSTRAINT IF EXISTS cash_session_close_four_eyes_chk;
ALTER TABLE nexora.cash_session ADD CONSTRAINT cash_session_close_four_eyes_chk
    CHECK (close_validated_by IS NULL OR close_validated_by <> user_id);

-- Une clôture finalisée n'est plus modifiable (§13) : montants, statut et validation sont figés.
CREATE OR REPLACE FUNCTION nexora.forbid_closed_cash_session_change()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status IN ('closed', 'reconciled') AND (
           NEW.expected_balance IS DISTINCT FROM OLD.expected_balance
        OR NEW.counted_balance  IS DISTINCT FROM OLD.counted_balance
        OR NEW.opening_balance  IS DISTINCT FROM OLD.opening_balance
        OR NEW.closed_at        IS DISTINCT FROM OLD.closed_at
        OR NEW.user_id          IS DISTINCT FROM OLD.user_id
        OR NEW.close_validated_by IS DISTINCT FROM OLD.close_validated_by
        OR (NEW.status <> OLD.status AND NOT (OLD.status = 'closed' AND NEW.status = 'reconciled'))) THEN
        RAISE EXCEPTION 'Session de caisse % clôturée : modification interdite (guide §13).', OLD.id
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_cash_session_closed_guard ON nexora.cash_session;
CREATE TRIGGER trg_cash_session_closed_guard BEFORE UPDATE ON nexora.cash_session
    FOR EACH ROW EXECUTE FUNCTION nexora.forbid_closed_cash_session_change();

INSERT INTO nexora.setting_definition (key, category, value_type, default_value, allowed_values, max_scope, is_sensitive, affects_future, description) VALUES
    ('pos.cash_variance_threshold', 'pos', 'number',  '500',   '[]', 'establishment', false, false,
     'Écart de caisse (valeur absolue, devise de la caisse) au-delà duquel la clôture exige une validation Manager'),
    ('pos.require_cash_session',    'pos', 'boolean', 'false', '[]', 'establishment', false, false,
     'Exiger une session de caisse ouverte pour toute vente saisie au POS')
ON CONFLICT (key) DO NOTHING;
