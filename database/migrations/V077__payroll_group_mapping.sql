-- =============================================================================
-- NEXORA — V077 — Module paie : rattachement des comptes de paie au plan de groupe (Phase 9)
-- (docs/module-paie-rh-avances.md §32 ; comptes créés par V076)
--
-- V076 a ajouté 8 comptes de paie à chaque entreprise. Dans un groupe doté d'un plan de comptes consolidé,
-- un compte mouvementé sans rattachement rend la consolidation incomplète (« compte non mappé »). Cette
-- migration crée, pour chaque groupe qui a déjà un plan de groupe, trois comptes « Personnel » et y rattache
-- les comptes de paie de ses entreprises ; une entreprise créée plus tard dans un tel groupe est rattachée
-- par nexora.bootstrap_payroll_defaults. Aucun rattachement existant n'est modifié.
-- =============================================================================
SET search_path TO nexora, public;

CREATE OR REPLACE FUNCTION nexora.bootstrap_payroll_group_mapping(p_company uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_group uuid;
BEGIN
    SELECT group_id INTO v_group FROM nexora.company WHERE id = p_company;
    -- Seulement pour un groupe qui tient déjà un plan de comptes consolidé.
    IF v_group IS NULL OR NOT EXISTS (SELECT 1 FROM nexora.group_account WHERE group_id = v_group) THEN
        RETURN;
    END IF;

    INSERT INTO nexora.group_account (group_id, code, name, account_type, sort_order)
    VALUES (v_group, 'GA-PERS',    'Personnel — dettes sociales et salariales', 'liability', 900),
           (v_group, 'GA-PERSREC', 'Personnel — avances et prêts',             'asset',     901),
           (v_group, 'GA-PERSEXP', 'Charges de personnel',                     'expense',   902)
    ON CONFLICT (group_id, code) DO NOTHING;

    INSERT INTO nexora.account_group_mapping (group_id, company_id, account_id, group_account_id, effective_from, status)
    SELECT v_group, a.company_id, a.id, ga.id, DATE '2000-01-01', 'active'
    FROM nexora.account a
    JOIN (VALUES ('4210', 'GA-PERS'), ('4280', 'GA-PERS'), ('4286', 'GA-PERS'), ('4310', 'GA-PERS'),
                 ('4250', 'GA-PERSREC'), ('4251', 'GA-PERSREC'),
                 ('6410', 'GA-PERSEXP'), ('6450', 'GA-PERSEXP')) AS m(local_code, group_code) ON m.local_code = a.code
    JOIN nexora.group_account ga ON ga.group_id = v_group AND ga.code = m.group_code
    WHERE a.company_id = p_company
      AND NOT EXISTS (SELECT 1 FROM nexora.account_group_mapping x WHERE x.account_id = a.id AND x.status = 'active');
END;
$$;
GRANT EXECUTE ON FUNCTION nexora.bootstrap_payroll_group_mapping(uuid) TO nexora_app;

SELECT nexora.bootstrap_payroll_group_mapping(c.id) FROM nexora.company c WHERE c.group_id IS NOT NULL;

-- Toute entreprise initialisée ensuite (assistant, création d'entreprise dans un groupe) est rattachée aussi.
CREATE OR REPLACE FUNCTION nexora.bootstrap_company_defaults(p_company uuid, p_currency uuid, p_year int)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    PERFORM nexora.bootstrap_company_defaults_v072(p_company, p_currency, p_year);
    PERFORM nexora.bootstrap_payroll_defaults(p_company);
    PERFORM nexora.bootstrap_payroll_group_mapping(p_company);
END;
$$;
