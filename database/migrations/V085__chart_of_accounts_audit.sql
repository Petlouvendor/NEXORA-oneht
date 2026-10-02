-- =============================================================================
-- NEXORA — V085 — Plan comptable géré depuis l'application (finalisation produit, phase 8)
--
-- Le plan comptable devient modifiable par l'interface (création, intitulé, regroupement, désactivation) :
-- chaque changement est tracé dans le journal d'audit, comme pour les écritures et les périodes.
-- Unicité du code de compte insensible à la casse au sein d'une entreprise.
-- Idempotente.
-- =============================================================================
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['account', 'chart_of_accounts', 'journal', 'account_mapping'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS account_company_code_ci_uk ON nexora.account (company_id, lower(code));
