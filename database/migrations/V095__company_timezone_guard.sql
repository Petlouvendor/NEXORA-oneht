-- =============================================================================
-- NEXORA — V095 — Fuseau horaire de l'entreprise : date métier (analyse exhaustive 2026-10, §5.5)
--
-- La date du jour d'une opération suit désormais le fuseau de l'entreprise (company.timezone) : côté
-- application (IBusinessCalendar) et côté base, où la session PostgreSQL prend ce fuseau à l'ouverture de
-- connexion. Un fuseau inconnu ferait échouer chaque connexion de l'entreprise : il est donc refusé à
-- l'écriture, et les valeurs existantes inconnues sont ramenées sur Haïti.
-- Idempotente.
-- =============================================================================
UPDATE nexora.company
   SET timezone = 'America/Port-au-Prince'
 WHERE timezone IS NULL
    OR timezone NOT IN (SELECT name FROM pg_timezone_names);

CREATE OR REPLACE FUNCTION nexora.company_timezone_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.timezone IS NULL OR NOT EXISTS (SELECT 1 FROM pg_timezone_names WHERE name = NEW.timezone) THEN
        RAISE EXCEPTION 'Fuseau horaire inconnu : %', NEW.timezone
            USING ERRCODE = '22023', HINT = 'Utilisez un identifiant IANA, par exemple America/Port-au-Prince.';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS company_timezone_guard ON nexora.company;
CREATE TRIGGER company_timezone_guard
    BEFORE INSERT OR UPDATE OF timezone ON nexora.company
    FOR EACH ROW EXECUTE FUNCTION nexora.company_timezone_guard();
