-- =============================================================================
-- NEXORA — V075 — Module « Paie, RH & avances sur salaire » — schéma
-- (docs/module-paie-rh-avances.md ; mise en œuvre : docs/module-paie-rh-avances-implementation.md)
--
-- Sous-système financier contrôlé (§1) :
--   • dossier employé, départements, contrats, historique de rémunération IMMUABLE (§7, §8) ;
--   • catalogue des composants de paie, éléments récurrents et variables ponctuelles (§10, §20) ;
--   • présences, congés/absences, heures supplémentaires avec validation 4-yeux (§18, §19) ;
--   • règles légales/sociales CONFIGURABLES et VERSIONNÉES, jamais codées en dur (§11, §31) ;
--   • périodes → exécutions → lignes par employé → gains / retenues explicables (§9-§11, §30) ;
--   • avances, acomptes et prêts au personnel, échéancier, récupérations immuables (§12-§17) ;
--   • remboursements de frais (dette de l'entreprise envers l'employé, §17, §32) ;
--   • paiements des salaires (une instruction = un paiement, §24, §39), bulletins figés (§25) ;
--   • régularisations reliées à la paie d'origine (§36), documents RH confidentiels (§7).
-- Verrous portés par la base (§22, §31, §39) : une paie validée n'est plus modifiable, une récupération
-- ne dépasse jamais le solde, un salaire ne s'écrase pas, rien de finalisé ne se supprime.
-- Les permissions, rôles, paramètres, comptes et modèles de règles sont posés par V076.
-- =============================================================================
SET search_path TO nexora, public;

-- =====================================================================
-- 0. Garde générique : suppression physique interdite (§35, §39)
-- =====================================================================
CREATE OR REPLACE FUNCTION nexora.hr_forbid_delete()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF nexora.flag('allow_hard_delete') THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION '% % : suppression physique interdite (historique RH/paie conservé)', TG_TABLE_NAME, OLD.id
        USING ERRCODE = 'check_violation';
END;
$$;

-- =====================================================================
-- 1. Départements & dossier employé (§7)
-- =====================================================================
CREATE TABLE nexora.department (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid NOT NULL REFERENCES nexora.company(id),
    code                 text NOT NULL,
    name                 text NOT NULL,
    manager_employee_id  uuid,
    status               text NOT NULL DEFAULT 'active',
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT department_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT department_code_uk UNIQUE (company_id, code),
    CONSTRAINT department_status_chk CHECK (status IN ('active', 'inactive'))
);

CREATE TABLE nexora.employee (
    id                         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id                 uuid NOT NULL REFERENCES nexora.company(id),
    employee_number            text NOT NULL,                       -- matricule
    first_name                 text NOT NULL,
    last_name                  text NOT NULL,
    birth_date                 date,
    phone                      text,
    email                      text,
    address                    text,
    city                       text,
    emergency_contact_name     text,
    emergency_contact_phone    text,
    job_title                  text,
    department_id              uuid,
    manager_employee_id        uuid,
    establishment_id           uuid,                                -- lieu de travail
    hire_date                  date NOT NULL,
    termination_date           date,
    termination_reason         text,
    status                     text NOT NULL DEFAULT 'active',
    user_id                    uuid REFERENCES nexora.app_user(id),  -- espace employé (§5)
    -- Paiement (§7 « Banque ») : numéros chiffrés AES-256-GCM, seuls les 4 derniers chiffres en clair.
    payment_method             text NOT NULL DEFAULT 'bank_transfer',
    bank_name                  text,
    bank_account_encrypted     text,
    bank_account_last4         text,
    mobile_money_encrypted     text,
    mobile_money_last4         text,
    -- Fiscal / social (§7) : identifiants chiffrés + paramètres propres à la juridiction.
    tax_id_encrypted           text,
    tax_id_last4               text,
    social_id_encrypted        text,
    social_id_last4            text,
    tax_parameters             jsonb NOT NULL DEFAULT '{}'::jsonb,
    notes                      text,
    created_by                 uuid REFERENCES nexora.app_user(id),
    updated_by                 uuid REFERENCES nexora.app_user(id),
    created_at                 timestamptz NOT NULL DEFAULT now(),
    updated_at                 timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT employee_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT employee_number_uk UNIQUE (company_id, employee_number),
    CONSTRAINT employee_department_fk FOREIGN KEY (department_id, company_id)
        REFERENCES nexora.department (id, company_id),
    CONSTRAINT employee_manager_fk FOREIGN KEY (manager_employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT employee_establishment_fk FOREIGN KEY (establishment_id, company_id)
        REFERENCES nexora.establishment (id, company_id),
    CONSTRAINT employee_status_chk CHECK (status IN ('active', 'suspended', 'on_leave', 'terminated')),
    CONSTRAINT employee_payment_method_chk
        CHECK (payment_method IN ('bank_transfer', 'cash', 'mobile_money', 'check', 'other')),
    CONSTRAINT employee_terminated_chk CHECK (status <> 'terminated' OR termination_date IS NOT NULL),
    CONSTRAINT employee_dates_chk CHECK (termination_date IS NULL OR termination_date >= hire_date),
    CONSTRAINT employee_not_own_manager_chk CHECK (manager_employee_id IS NULL OR manager_employee_id <> id),
    CONSTRAINT employee_names_chk CHECK (length(btrim(first_name)) > 0 AND length(btrim(last_name)) > 0)
);
CREATE UNIQUE INDEX employee_user_uk ON nexora.employee (company_id, user_id) WHERE user_id IS NOT NULL;
CREATE INDEX employee_status_idx ON nexora.employee (company_id, status);
CREATE INDEX employee_department_idx ON nexora.employee (department_id);
CREATE INDEX employee_manager_idx ON nexora.employee (manager_employee_id);
CREATE INDEX employee_search_trgm_idx ON nexora.employee
    USING gin (nexora.search_norm(first_name || ' ' || last_name || ' ' || employee_number) gin_trgm_ops);

ALTER TABLE nexora.department
    ADD CONSTRAINT department_manager_fk FOREIGN KEY (manager_employee_id, company_id)
        REFERENCES nexora.employee (id, company_id);

-- Équipe d'un utilisateur (§6 « Manager ») : employés dont il est le responsable, directement ou non.
CREATE OR REPLACE FUNCTION nexora.employee_team(p_company uuid, p_user uuid)
RETURNS SETOF uuid LANGUAGE sql STABLE AS $$
    WITH RECURSIVE team AS (
        SELECT e.id FROM nexora.employee e
        WHERE e.company_id = p_company
          AND e.manager_employee_id IN (SELECT m.id FROM nexora.employee m
                                        WHERE m.company_id = p_company AND m.user_id = p_user)
        UNION
        SELECT e.id FROM nexora.employee e JOIN team t ON e.manager_employee_id = t.id
        WHERE e.company_id = p_company
    )
    SELECT id FROM team;
$$;

-- =====================================================================
-- 2. Contrats & historique de rémunération (§8)
-- =====================================================================
CREATE TABLE nexora.employee_contract (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid NOT NULL REFERENCES nexora.company(id),
    employee_id      uuid NOT NULL,
    contract_number  text NOT NULL,
    contract_type    text NOT NULL,
    start_date       date NOT NULL,
    end_date         date,
    trial_end_date   date,
    pay_frequency    text NOT NULL DEFAULT 'monthly',
    pay_basis        text NOT NULL DEFAULT 'monthly',
    currency_id      uuid NOT NULL REFERENCES nexora.currency(id),
    weekly_hours     numeric(6,2) NOT NULL DEFAULT 48,
    job_title        text,
    benefits         text,
    conditions       text,
    status           text NOT NULL DEFAULT 'active',
    ended_at         timestamptz,
    ended_by         uuid REFERENCES nexora.app_user(id),
    end_reason       text,
    created_by       uuid REFERENCES nexora.app_user(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT employee_contract_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT employee_contract_number_uk UNIQUE (company_id, contract_number),
    CONSTRAINT employee_contract_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT employee_contract_type_chk CHECK (contract_type IN (
        'permanent', 'fixed_term', 'temporary', 'internship', 'apprenticeship', 'consultant', 'daily')),
    CONSTRAINT employee_contract_frequency_chk CHECK (pay_frequency IN ('monthly', 'biweekly', 'weekly')),
    CONSTRAINT employee_contract_basis_chk CHECK (pay_basis IN ('monthly', 'daily', 'hourly')),
    CONSTRAINT employee_contract_hours_chk CHECK (weekly_hours > 0 AND weekly_hours <= 84),
    CONSTRAINT employee_contract_status_chk CHECK (status IN ('draft', 'active', 'ended', 'terminated', 'cancelled')),
    CONSTRAINT employee_contract_dates_chk CHECK (end_date IS NULL OR end_date >= start_date),
    CONSTRAINT employee_contract_trial_chk CHECK (trial_end_date IS NULL OR trial_end_date >= start_date),
    CONSTRAINT employee_contract_fixed_term_chk CHECK (contract_type <> 'fixed_term' OR end_date IS NOT NULL),
    CONSTRAINT employee_contract_end_chk CHECK (
        status NOT IN ('ended', 'terminated') OR (end_date IS NOT NULL AND length(btrim(coalesce(end_reason, ''))) > 0)),
    -- Deux contrats actifs ne se chevauchent jamais pour un même employé.
    CONSTRAINT employee_contract_no_overlap EXCLUDE USING gist (
        employee_id WITH =,
        daterange(start_date, coalesce(end_date, 'infinity'::date), '[]') WITH &&)
        WHERE (status IN ('active', 'ended', 'terminated'))
);
CREATE INDEX employee_contract_employee_idx ON nexora.employee_contract (employee_id, start_date DESC);

CREATE TABLE nexora.salary_history (
    id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id               uuid NOT NULL REFERENCES nexora.company(id),
    employee_id              uuid NOT NULL,
    contract_id              uuid NOT NULL,
    effective_date           date NOT NULL,
    pay_basis                text NOT NULL,
    amount                   nexora.money_amount NOT NULL,
    hourly_rate              nexora.money_amount,           -- taux horaire imposé (sinon dérivé)
    currency_id              uuid NOT NULL REFERENCES nexora.currency(id),
    previous_amount          nexora.money_amount,           -- ancien salaire (§8), posé par la base
    previous_effective_date  date,
    reason                   text NOT NULL,
    created_by               uuid REFERENCES nexora.app_user(id),
    created_at               timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT salary_history_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT salary_history_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT salary_history_contract_fk FOREIGN KEY (contract_id, company_id)
        REFERENCES nexora.employee_contract (id, company_id),
    CONSTRAINT salary_history_contract_date_uk UNIQUE (contract_id, effective_date),
    CONSTRAINT salary_history_amount_chk CHECK (amount > 0),
    CONSTRAINT salary_history_hourly_chk CHECK (hourly_rate IS NULL OR hourly_rate > 0),
    CONSTRAINT salary_history_basis_chk CHECK (pay_basis IN ('monthly', 'daily', 'hourly')),
    CONSTRAINT salary_history_reason_chk CHECK (length(btrim(reason)) > 0)
);
CREATE INDEX salary_history_employee_idx ON nexora.salary_history (employee_id, effective_date DESC);

-- Ancien salaire posé par la base : aucun chemin d'écriture ne peut l'oublier.
CREATE OR REPLACE FUNCTION nexora.salary_history_defaults()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE prev record;
BEGIN
    SELECT amount, effective_date INTO prev FROM nexora.salary_history
     WHERE employee_id = NEW.employee_id AND effective_date < NEW.effective_date
     ORDER BY effective_date DESC, created_at DESC LIMIT 1;
    NEW.previous_amount := prev.amount;
    NEW.previous_effective_date := prev.effective_date;
    NEW.created_by := coalesce(NEW.created_by, nexora.current_user_id());
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_salary_history_defaults BEFORE INSERT ON nexora.salary_history
    FOR EACH ROW EXECUTE FUNCTION nexora.salary_history_defaults();

-- Historique immuable (§8 « ne doit pas écraser l'ancien historique »).
CREATE OR REPLACE FUNCTION nexora.hr_immutable_row()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' AND nexora.flag('allow_hard_delete') THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION '% % : ligne immuable (% interdit) — créer une nouvelle entrée', TG_TABLE_NAME,
        coalesce(OLD.id, NEW.id), TG_OP USING ERRCODE = 'check_violation';
END;
$$;
CREATE TRIGGER trg_salary_history_immutable BEFORE UPDATE OR DELETE ON nexora.salary_history
    FOR EACH ROW EXECUTE FUNCTION nexora.hr_immutable_row();

-- Un contrat non brouillon ne se supprime jamais (§8).
CREATE OR REPLACE FUNCTION nexora.employee_contract_forbid_delete()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status = 'draft' OR nexora.flag('allow_hard_delete') THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION 'Contrat % : suppression physique interdite (le terminer)', OLD.contract_number
        USING ERRCODE = 'check_violation';
END;
$$;
CREATE TRIGGER trg_employee_contract_forbid_delete BEFORE DELETE ON nexora.employee_contract
    FOR EACH ROW EXECUTE FUNCTION nexora.employee_contract_forbid_delete();
CREATE TRIGGER trg_employee_forbid_delete BEFORE DELETE ON nexora.employee
    FOR EACH ROW EXECUTE FUNCTION nexora.hr_forbid_delete();

-- =====================================================================
-- 3. Composants de paie, éléments récurrents, variables (§10, §20, §21)
-- =====================================================================
CREATE TABLE nexora.pay_component (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid REFERENCES nexora.company(id),     -- NULL = composant système
    code               text NOT NULL,
    name               text NOT NULL,
    kind               text NOT NULL,                          -- earning | deduction
    category           text NOT NULL,
    taxable            boolean NOT NULL DEFAULT true,          -- entre dans l'assiette fiscale
    subject_to_social  boolean NOT NULL DEFAULT true,          -- entre dans l'assiette sociale
    in_gross           boolean NOT NULL DEFAULT true,          -- compté dans le brut
    cash               boolean NOT NULL DEFAULT true,          -- payé en argent (faux : avantage en nature)
    requires_legal_basis boolean NOT NULL DEFAULT false,       -- retenue disciplinaire (§21)
    default_amount     nexora.money_amount,
    status             text NOT NULL DEFAULT 'active',
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pay_component_uk UNIQUE NULLS NOT DISTINCT (company_id, code),
    CONSTRAINT pay_component_kind_chk CHECK (kind IN ('earning', 'deduction')),
    CONSTRAINT pay_component_category_chk CHECK (category IN (
        'base', 'overtime', 'bonus', 'commission', 'allowance', 'benefit_in_kind', 'reimbursement', 'adjustment',
        'absence', 'advance', 'loan', 'other_deduction', 'tax', 'social')),
    CONSTRAINT pay_component_kind_category_chk CHECK (
        (kind = 'earning' AND category IN ('base','overtime','bonus','commission','allowance','benefit_in_kind','reimbursement','adjustment'))
     OR (kind = 'deduction' AND category IN ('absence','advance','loan','other_deduction','tax','social','adjustment'))),
    CONSTRAINT pay_component_status_chk CHECK (status IN ('active', 'inactive'))
);

CREATE TABLE nexora.employee_pay_element (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id    uuid NOT NULL REFERENCES nexora.company(id),
    employee_id   uuid NOT NULL,
    component_id  uuid NOT NULL REFERENCES nexora.pay_component(id),
    calc_method   text NOT NULL DEFAULT 'fixed',
    amount        nexora.money_amount,
    rate          numeric(9,4),                                 -- % (commission, % du salaire)
    currency_id   uuid NOT NULL REFERENCES nexora.currency(id),
    start_date    date NOT NULL,
    end_date      date,
    note          text,
    status        text NOT NULL DEFAULT 'active',
    created_by    uuid REFERENCES nexora.app_user(id),
    ended_by      uuid REFERENCES nexora.app_user(id),
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT employee_pay_element_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT employee_pay_element_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT employee_pay_element_method_chk
        CHECK (calc_method IN ('fixed', 'percent_of_base', 'percent_of_sales', 'per_day_worked')),
    CONSTRAINT employee_pay_element_value_chk CHECK (
        (calc_method IN ('fixed', 'per_day_worked') AND amount IS NOT NULL AND amount > 0)
     OR (calc_method IN ('percent_of_base', 'percent_of_sales') AND rate IS NOT NULL AND rate > 0 AND rate <= 100)),
    CONSTRAINT employee_pay_element_dates_chk CHECK (end_date IS NULL OR end_date >= start_date),
    CONSTRAINT employee_pay_element_status_chk CHECK (status IN ('active', 'ended'))
);
CREATE INDEX employee_pay_element_employee_idx ON nexora.employee_pay_element (employee_id, status);
CREATE TRIGGER trg_employee_pay_element_forbid_delete BEFORE DELETE ON nexora.employee_pay_element
    FOR EACH ROW EXECUTE FUNCTION nexora.hr_forbid_delete();

-- =====================================================================
-- 4. Périodes & exécutions de paie (§9, §22)
-- =====================================================================
CREATE TABLE nexora.payroll_period (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid NOT NULL REFERENCES nexora.company(id),
    code            text NOT NULL,
    label           text NOT NULL,
    kind            text NOT NULL DEFAULT 'regular',          -- regular | off_cycle (régularisations hors cycle)
    frequency       text NOT NULL DEFAULT 'monthly',
    start_date      date NOT NULL,
    end_date        date NOT NULL,
    cutoff_date     date NOT NULL,                             -- variables verrouillées après cette date
    payment_date    date NOT NULL,
    employee_scope  uuid[] NOT NULL DEFAULT '{}',              -- hors cycle : employés visés
    status          text NOT NULL DEFAULT 'draft',
    closed_at       timestamptz,
    closed_by       uuid REFERENCES nexora.app_user(id),
    cancelled_at    timestamptz,
    cancelled_by    uuid REFERENCES nexora.app_user(id),
    cancel_reason   text,
    created_by      uuid REFERENCES nexora.app_user(id),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payroll_period_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT payroll_period_code_uk UNIQUE (company_id, code),
    CONSTRAINT payroll_period_kind_chk CHECK (kind IN ('regular', 'off_cycle')),
    CONSTRAINT payroll_period_frequency_chk CHECK (frequency IN ('monthly', 'biweekly', 'weekly')),
    CONSTRAINT payroll_period_dates_chk CHECK (end_date >= start_date AND end_date - start_date <= 31),
    CONSTRAINT payroll_period_cutoff_chk CHECK (cutoff_date >= start_date),
    CONSTRAINT payroll_period_payment_chk CHECK (payment_date >= start_date),
    CONSTRAINT payroll_period_status_chk CHECK (status IN (
        'draft', 'calculated', 'in_validation', 'approved', 'paid', 'closed', 'cancelled')),
    CONSTRAINT payroll_period_cancel_chk CHECK (
        status <> 'cancelled' OR (cancelled_at IS NOT NULL AND length(btrim(coalesce(cancel_reason, ''))) > 0)),
    -- Deux périodes régulières d'une même fréquence ne se chevauchent pas (§23 « doublons absents »).
    CONSTRAINT payroll_period_no_overlap EXCLUDE USING gist (
        company_id WITH =, frequency WITH =, daterange(start_date, end_date, '[]') WITH &&)
        WHERE (kind = 'regular' AND status <> 'cancelled')
);
CREATE INDEX payroll_period_company_idx ON nexora.payroll_period (company_id, start_date DESC);

CREATE TABLE nexora.payroll_run (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          uuid NOT NULL REFERENCES nexora.company(id),
    period_id           uuid NOT NULL,
    run_number          text NOT NULL,
    status              text NOT NULL DEFAULT 'draft',
    stale               boolean NOT NULL DEFAULT false,         -- données modifiées depuis le calcul
    calculation_count   integer NOT NULL DEFAULT 0,
    calculated_by       uuid REFERENCES nexora.app_user(id),
    calculated_at       timestamptz,
    reviewed_by         uuid REFERENCES nexora.app_user(id),
    reviewed_at         timestamptz,
    review_note         text,
    warnings_acknowledged boolean NOT NULL DEFAULT false,
    validated_by        uuid REFERENCES nexora.app_user(id),
    validated_at        timestamptz,
    validation_note     text,
    approved_by         uuid REFERENCES nexora.app_user(id),
    approved_at         timestamptz,
    approval_note       text,
    journal_entry_id    uuid,                                    -- obligations salariales (§32)
    posting_operation_uuid uuid,
    paid_at             timestamptz,
    closed_by           uuid REFERENCES nexora.app_user(id),
    closed_at           timestamptz,
    returned_note       text,                                    -- dernier renvoi pour correction
    cancelled_by        uuid REFERENCES nexora.app_user(id),
    cancelled_at        timestamptz,
    cancel_reason       text,
    created_by          uuid REFERENCES nexora.app_user(id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payroll_run_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT payroll_run_number_uk UNIQUE (company_id, run_number),
    CONSTRAINT payroll_run_period_fk FOREIGN KEY (period_id, company_id)
        REFERENCES nexora.payroll_period (id, company_id),
    CONSTRAINT payroll_run_status_chk CHECK (status IN (
        'draft', 'calculated', 'reviewed', 'validated', 'approved', 'paid', 'closed', 'cancelled')),
    CONSTRAINT payroll_run_reviewed_chk CHECK (
        status NOT IN ('reviewed', 'validated', 'approved', 'paid', 'closed') OR (reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL)),
    CONSTRAINT payroll_run_validated_chk CHECK (
        status NOT IN ('validated', 'approved', 'paid', 'closed') OR (validated_by IS NOT NULL AND validated_at IS NOT NULL)),
    CONSTRAINT payroll_run_approved_chk CHECK (
        status NOT IN ('approved', 'paid', 'closed') OR (approved_by IS NOT NULL AND approved_at IS NOT NULL)),
    CONSTRAINT payroll_run_cancel_chk CHECK (
        status <> 'cancelled' OR (cancelled_at IS NOT NULL AND length(btrim(coalesce(cancel_reason, ''))) > 0)),
    CONSTRAINT payroll_run_posting_uk UNIQUE (posting_operation_uuid)
);
-- Une seule exécution vivante par période.
CREATE UNIQUE INDEX payroll_run_period_active_uk ON nexora.payroll_run (period_id) WHERE status <> 'cancelled';
CREATE INDEX payroll_run_status_idx ON nexora.payroll_run (company_id, status);

-- Une paie approuvée ne revient jamais en arrière, ne s'annule plus, ne se supprime pas (§22, §39).
CREATE OR REPLACE FUNCTION nexora.payroll_run_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF nexora.flag('allow_hard_delete') THEN RETURN OLD; END IF;
        RAISE EXCEPTION 'Paie % : suppression physique interdite', OLD.run_number USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status IN ('approved', 'paid', 'closed') AND NEW.status NOT IN ('approved', 'paid', 'closed') THEN
        RAISE EXCEPTION 'Paie % approuvée : retour en arrière ou annulation interdits — passer par une régularisation',
            OLD.run_number USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status = 'closed' AND (NEW.status <> 'closed' OR NEW.journal_entry_id IS DISTINCT FROM OLD.journal_entry_id) THEN
        RAISE EXCEPTION 'Paie % clôturée : modification interdite', OLD.run_number USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status = 'cancelled' AND NEW.status <> 'cancelled' THEN
        RAISE EXCEPTION 'Paie % annulée : réouverture interdite', OLD.run_number USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.journal_entry_id IS NOT NULL AND NEW.journal_entry_id IS DISTINCT FROM OLD.journal_entry_id THEN
        RAISE EXCEPTION 'Paie % : écriture de paie déjà liée', OLD.run_number USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_payroll_run_guard BEFORE UPDATE OR DELETE ON nexora.payroll_run
    FOR EACH ROW EXECUTE FUNCTION nexora.payroll_run_guard();
CREATE TRIGGER trg_payroll_period_forbid_delete BEFORE DELETE ON nexora.payroll_period
    FOR EACH ROW EXECUTE FUNCTION nexora.hr_forbid_delete();

-- =====================================================================
-- 5. Lignes de paie par employé, gains et retenues explicables (§10, §11, §30)
-- =====================================================================
CREATE TABLE nexora.payroll_item (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id              uuid NOT NULL REFERENCES nexora.company(id),
    run_id                  uuid NOT NULL,
    period_id               uuid NOT NULL,
    employee_id             uuid NOT NULL,
    contract_id             uuid,
    salary_history_id       uuid,
    currency_id             uuid NOT NULL REFERENCES nexora.currency(id),
    -- Instantané du dossier au moment du calcul (bulletin, rapports par département).
    employee_number         text NOT NULL,
    employee_name           text NOT NULL,
    job_title               text,
    department_id           uuid,
    department_name         text,
    contract_type           text,
    pay_basis               text,
    pay_frequency           text,
    payment_method          text,
    -- Assiettes et totaux (types exacts, devise explicite — §31).
    base_salary             nexora.money_amount NOT NULL DEFAULT 0,   -- salaire contractuel de référence
    period_days             integer NOT NULL DEFAULT 0,
    days_employed           numeric(6,2) NOT NULL DEFAULT 0,
    absence_days            numeric(6,2) NOT NULL DEFAULT 0,
    prorata                 numeric(9,6) NOT NULL DEFAULT 0,
    daily_rate              nexora.money_amount NOT NULL DEFAULT 0,
    hourly_rate             nexora.money_amount NOT NULL DEFAULT 0,
    base_amount             nexora.money_amount NOT NULL DEFAULT 0,   -- salaire de base proratisé
    gross_amount            nexora.money_amount NOT NULL DEFAULT 0,
    reimbursement_amount    nexora.money_amount NOT NULL DEFAULT 0,   -- frais remboursés (hors brut)
    taxable_base            nexora.money_amount NOT NULL DEFAULT 0,
    social_base             nexora.money_amount NOT NULL DEFAULT 0,
    legal_deductions        nexora.money_amount NOT NULL DEFAULT 0,   -- impôts + cotisations salariales
    absence_deduction       nexora.money_amount NOT NULL DEFAULT 0,
    benefit_offset          nexora.money_amount NOT NULL DEFAULT 0,   -- avantages en nature (non versés)
    advance_recovery        nexora.money_amount NOT NULL DEFAULT 0,
    loan_recovery           nexora.money_amount NOT NULL DEFAULT 0,
    other_deductions        nexora.money_amount NOT NULL DEFAULT 0,
    total_deductions        nexora.money_amount NOT NULL DEFAULT 0,
    net_amount              nexora.money_amount NOT NULL DEFAULT 0,
    employer_contributions  nexora.money_amount NOT NULL DEFAULT 0,
    employer_cost           nexora.money_amount NOT NULL DEFAULT 0,
    status                  text NOT NULL DEFAULT 'calculated',
    excluded_reason         text,
    anomalies               jsonb NOT NULL DEFAULT '[]'::jsonb,
    blocking_count          integer NOT NULL DEFAULT 0,
    warning_count           integer NOT NULL DEFAULT 0,
    trace                   jsonb NOT NULL DEFAULT '[]'::jsonb,       -- explication pas à pas (§11, §40)
    rules_snapshot          jsonb NOT NULL DEFAULT '[]'::jsonb,       -- règles et versions appliquées
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payroll_item_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT payroll_item_run_fk FOREIGN KEY (run_id, company_id) REFERENCES nexora.payroll_run (id, company_id),
    CONSTRAINT payroll_item_period_fk FOREIGN KEY (period_id, company_id) REFERENCES nexora.payroll_period (id, company_id),
    CONSTRAINT payroll_item_employee_fk FOREIGN KEY (employee_id, company_id) REFERENCES nexora.employee (id, company_id),
    CONSTRAINT payroll_item_contract_fk FOREIGN KEY (contract_id, company_id) REFERENCES nexora.employee_contract (id, company_id),
    CONSTRAINT payroll_item_run_employee_uk UNIQUE (run_id, employee_id),
    CONSTRAINT payroll_item_status_chk CHECK (status IN ('calculated', 'excluded', 'approved', 'paid', 'cancelled')),
    CONSTRAINT payroll_item_excluded_chk CHECK (status <> 'excluded' OR length(btrim(coalesce(excluded_reason, ''))) > 0),
    CONSTRAINT payroll_item_amounts_chk CHECK (
        gross_amount >= 0 AND total_deductions >= 0 AND employer_contributions >= 0 AND reimbursement_amount >= 0),
    -- Net = brut + frais remboursés − total des retenues (§11), vérifié par la base.
    CONSTRAINT payroll_item_net_chk CHECK (net_amount = gross_amount + reimbursement_amount - total_deductions),
    CONSTRAINT payroll_item_total_chk CHECK (total_deductions = legal_deductions + absence_deduction + benefit_offset
        + advance_recovery + loan_recovery + other_deductions),
    CONSTRAINT payroll_item_net_positive_chk CHECK (status NOT IN ('approved', 'paid') OR net_amount >= 0)
);
CREATE INDEX payroll_item_employee_idx ON nexora.payroll_item (employee_id, created_at DESC);
CREATE INDEX payroll_item_period_idx ON nexora.payroll_item (period_id);

CREATE TABLE nexora.payroll_earning (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid NOT NULL REFERENCES nexora.company(id),
    payroll_item_id    uuid NOT NULL,
    component_id       uuid REFERENCES nexora.pay_component(id),
    code               text NOT NULL,
    category           text NOT NULL,
    label              text NOT NULL,
    quantity           numeric(12,4),
    rate               numeric(19,6),
    base_amount        nexora.money_amount,
    amount             nexora.money_amount NOT NULL,
    taxable            boolean NOT NULL,
    subject_to_social  boolean NOT NULL,
    in_gross           boolean NOT NULL,
    cash               boolean NOT NULL,
    source_type        text NOT NULL,
    source_id          uuid,
    sort_order         integer NOT NULL DEFAULT 0,
    created_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payroll_earning_item_fk FOREIGN KEY (payroll_item_id, company_id)
        REFERENCES nexora.payroll_item (id, company_id) ON DELETE CASCADE,
    CONSTRAINT payroll_earning_amount_chk CHECK (amount >= 0),
    CONSTRAINT payroll_earning_category_chk CHECK (category IN (
        'base','overtime','bonus','commission','allowance','benefit_in_kind','reimbursement','adjustment')),
    CONSTRAINT payroll_earning_source_chk CHECK (source_type IN (
        'contract','pay_element','variable','overtime','commission','adjustment','expense_claim'))
);
CREATE INDEX payroll_earning_item_idx ON nexora.payroll_earning (payroll_item_id, sort_order);
CREATE INDEX payroll_earning_source_idx ON nexora.payroll_earning (source_type, source_id);

CREATE TABLE nexora.payroll_deduction (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid NOT NULL REFERENCES nexora.company(id),
    payroll_item_id    uuid NOT NULL,
    component_id       uuid REFERENCES nexora.pay_component(id),
    code               text NOT NULL,
    category           text NOT NULL,
    borne_by           text NOT NULL DEFAULT 'employee',        -- employer = cotisation patronale (hors net)
    label              text NOT NULL,
    base_amount        nexora.money_amount,
    rate               numeric(19,6),
    planned_amount     nexora.money_amount,                     -- montant prévu avant plafonnement
    amount             nexora.money_amount NOT NULL,
    source_type        text NOT NULL,
    source_id          uuid,
    rule_id            uuid,
    rule_version       integer,
    sort_order         integer NOT NULL DEFAULT 0,
    created_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payroll_deduction_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT payroll_deduction_item_fk FOREIGN KEY (payroll_item_id, company_id)
        REFERENCES nexora.payroll_item (id, company_id) ON DELETE CASCADE,
    CONSTRAINT payroll_deduction_amount_chk CHECK (amount >= 0),
    CONSTRAINT payroll_deduction_category_chk CHECK (category IN (
        'tax','social','absence','advance','loan','other','benefit_offset','adjustment','employer')),
    CONSTRAINT payroll_deduction_borne_chk CHECK (borne_by IN ('employee', 'employer')),
    CONSTRAINT payroll_deduction_employer_chk CHECK ((borne_by = 'employer') = (category = 'employer')),
    CONSTRAINT payroll_deduction_source_chk CHECK (source_type IN (
        'rule','leave','attendance','advance','pay_element','variable','adjustment','benefit_in_kind')),
    -- §21 : chaque retenue a une source et, pour une retenue légale, une règle.
    CONSTRAINT payroll_deduction_rule_chk CHECK (category NOT IN ('tax','social','employer') OR rule_id IS NOT NULL),
    CONSTRAINT payroll_deduction_advance_chk CHECK (category NOT IN ('advance','loan') OR source_id IS NOT NULL)
);
CREATE INDEX payroll_deduction_item_idx ON nexora.payroll_deduction (payroll_item_id, sort_order);
CREATE INDEX payroll_deduction_source_idx ON nexora.payroll_deduction (source_type, source_id);

-- Paie validée = verrouillée (§22, §27 test « devient verrouillée ») : aucune ligne ne s'ajoute, ne change
-- ni ne disparaît ; seul le statut d'une ligne employé avance (approuvée → payée).
CREATE OR REPLACE FUNCTION nexora.payroll_run_status_of_item(p_item uuid)
RETURNS text LANGUAGE sql STABLE AS $$
    SELECT r.status FROM nexora.payroll_item i JOIN nexora.payroll_run r ON r.id = i.run_id WHERE i.id = p_item;
$$;

CREATE OR REPLACE FUNCTION nexora.payroll_item_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_status text;
BEGIN
    SELECT status INTO v_status FROM nexora.payroll_run WHERE id = coalesce(NEW.run_id, OLD.run_id);
    IF v_status IN ('validated', 'approved', 'paid', 'closed', 'cancelled') THEN
        IF TG_OP = 'UPDATE'
           AND (to_jsonb(NEW) - ARRAY['status', 'updated_at']) = (to_jsonb(OLD) - ARRAY['status', 'updated_at'])
           AND (OLD.status, NEW.status) IN (('calculated', 'approved'), ('approved', 'paid'), ('paid', 'approved'),
                                            ('calculated', 'cancelled'), ('excluded', 'cancelled')) THEN
            RETURN NEW;
        END IF;
        IF TG_OP = 'DELETE' AND nexora.flag('allow_hard_delete') THEN RETURN OLD; END IF;
        RAISE EXCEPTION 'Paie verrouillée (statut %) : ligne employé non modifiable — passer par une régularisation', v_status
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN coalesce(NEW, OLD);
END;
$$;
CREATE TRIGGER trg_payroll_item_guard BEFORE INSERT OR UPDATE OR DELETE ON nexora.payroll_item
    FOR EACH ROW EXECUTE FUNCTION nexora.payroll_item_guard();

CREATE OR REPLACE FUNCTION nexora.payroll_line_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_status text;
BEGIN
    v_status := nexora.payroll_run_status_of_item(coalesce(NEW.payroll_item_id, OLD.payroll_item_id));
    IF v_status IN ('validated', 'approved', 'paid', 'closed', 'cancelled') THEN
        IF TG_OP = 'DELETE' AND nexora.flag('allow_hard_delete') THEN RETURN OLD; END IF;
        RAISE EXCEPTION 'Paie verrouillée (statut %) : gains et retenues non modifiables', v_status
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN coalesce(NEW, OLD);
END;
$$;
CREATE TRIGGER trg_payroll_earning_guard BEFORE INSERT OR UPDATE OR DELETE ON nexora.payroll_earning
    FOR EACH ROW EXECUTE FUNCTION nexora.payroll_line_guard();
CREATE TRIGGER trg_payroll_deduction_guard BEFORE INSERT OR UPDATE OR DELETE ON nexora.payroll_deduction
    FOR EACH ROW EXECUTE FUNCTION nexora.payroll_line_guard();

-- =====================================================================
-- 6. Temps : présences, congés/absences, heures supplémentaires, variables (§18-§20)
-- =====================================================================
CREATE TABLE nexora.attendance_record (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid NOT NULL REFERENCES nexora.company(id),
    employee_id        uuid NOT NULL,
    work_date          date NOT NULL,
    status             text NOT NULL DEFAULT 'present',
    hours_worked       numeric(5,2),
    justified          boolean NOT NULL DEFAULT false,
    source             text NOT NULL DEFAULT 'manual',
    note               text,
    validation_status  text NOT NULL DEFAULT 'pending',
    validated_by       uuid REFERENCES nexora.app_user(id),
    validated_at       timestamptz,
    created_by         uuid REFERENCES nexora.app_user(id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT attendance_record_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT attendance_record_day_uk UNIQUE (employee_id, work_date),
    CONSTRAINT attendance_record_status_chk
        CHECK (status IN ('present', 'absent', 'late', 'half_day', 'holiday', 'rest', 'leave')),
    CONSTRAINT attendance_record_hours_chk CHECK (hours_worked IS NULL OR (hours_worked >= 0 AND hours_worked <= 24)),
    CONSTRAINT attendance_record_source_chk CHECK (source IN ('manual', 'import', 'device')),
    CONSTRAINT attendance_record_validation_chk CHECK (validation_status IN ('pending', 'validated', 'rejected')),
    CONSTRAINT attendance_record_four_eyes_chk CHECK (validated_by IS NULL OR created_by IS NULL OR validated_by <> created_by)
);
CREATE INDEX attendance_record_company_date_idx ON nexora.attendance_record (company_id, work_date);

CREATE TABLE nexora.leave_record (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id     uuid NOT NULL REFERENCES nexora.company(id),
    employee_id    uuid NOT NULL,
    leave_type     text NOT NULL,
    start_date     date NOT NULL,
    end_date       date NOT NULL,
    days           numeric(6,2) NOT NULL,
    paid           boolean NOT NULL,
    reason         text,
    status         text NOT NULL DEFAULT 'requested',
    requested_by   uuid NOT NULL REFERENCES nexora.app_user(id),
    requested_at   timestamptz NOT NULL DEFAULT now(),
    decided_by     uuid REFERENCES nexora.app_user(id),
    decided_at     timestamptz,
    decision_note  text,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT leave_record_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT leave_record_type_chk CHECK (leave_type IN (
        'annual', 'sick', 'maternity', 'paternity', 'unpaid', 'justified_absence', 'unjustified_absence', 'other')),
    CONSTRAINT leave_record_dates_chk CHECK (end_date >= start_date),
    CONSTRAINT leave_record_days_chk CHECK (days > 0 AND days <= (end_date - start_date + 1)),
    CONSTRAINT leave_record_status_chk CHECK (status IN ('requested', 'approved', 'rejected', 'cancelled')),
    CONSTRAINT leave_record_four_eyes_chk CHECK (decided_by IS NULL OR decided_by <> requested_by),
    CONSTRAINT leave_record_decided_chk CHECK (
        status NOT IN ('approved', 'rejected') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CONSTRAINT leave_record_rejected_chk CHECK (status <> 'rejected' OR length(btrim(coalesce(decision_note, ''))) > 0)
);
CREATE INDEX leave_record_employee_idx ON nexora.leave_record (employee_id, start_date);
CREATE INDEX leave_record_status_idx ON nexora.leave_record (company_id, status);

CREATE TABLE nexora.overtime_record (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid NOT NULL REFERENCES nexora.company(id),
    employee_id      uuid NOT NULL,
    work_date        date NOT NULL,
    hours            numeric(5,2) NOT NULL,
    overtime_type    text NOT NULL DEFAULT 'standard',
    coefficient      numeric(6,3) NOT NULL,                     -- coefficient configuré, figé à la saisie
    reason           text,
    status           text NOT NULL DEFAULT 'requested',
    requested_by     uuid NOT NULL REFERENCES nexora.app_user(id),
    requested_at     timestamptz NOT NULL DEFAULT now(),
    decided_by       uuid REFERENCES nexora.app_user(id),       -- preuve de l'approbation (§19)
    decided_at       timestamptz,
    decision_note    text,
    payroll_item_id  uuid,                                      -- intégrée dans cette paie
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT overtime_record_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT overtime_record_hours_chk CHECK (hours > 0 AND hours <= 24),
    CONSTRAINT overtime_record_type_chk CHECK (overtime_type IN ('standard', 'night', 'weekend', 'holiday')),
    CONSTRAINT overtime_record_coefficient_chk CHECK (coefficient >= 1 AND coefficient <= 5),
    CONSTRAINT overtime_record_status_chk CHECK (status IN ('requested', 'approved', 'rejected', 'cancelled')),
    CONSTRAINT overtime_record_four_eyes_chk CHECK (decided_by IS NULL OR decided_by <> requested_by),
    CONSTRAINT overtime_record_decided_chk CHECK (
        status NOT IN ('approved', 'rejected') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CONSTRAINT overtime_record_rejected_chk CHECK (status <> 'rejected' OR length(btrim(coalesce(decision_note, ''))) > 0)
);
CREATE INDEX overtime_record_employee_idx ON nexora.overtime_record (employee_id, work_date);
CREATE INDEX overtime_record_status_idx ON nexora.overtime_record (company_id, status);

CREATE TABLE nexora.payroll_variable (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id       uuid NOT NULL REFERENCES nexora.company(id),
    employee_id      uuid NOT NULL,
    period_id        uuid NOT NULL,
    component_id     uuid NOT NULL REFERENCES nexora.pay_component(id),
    amount           nexora.money_amount NOT NULL,
    description      text NOT NULL,
    legal_basis      text,                                      -- obligatoire pour une retenue disciplinaire
    status           text NOT NULL DEFAULT 'submitted',
    submitted_by     uuid NOT NULL REFERENCES nexora.app_user(id),
    submitted_at     timestamptz NOT NULL DEFAULT now(),
    decided_by       uuid REFERENCES nexora.app_user(id),
    decided_at       timestamptz,
    decision_note    text,
    payroll_item_id  uuid,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payroll_variable_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT payroll_variable_period_fk FOREIGN KEY (period_id, company_id)
        REFERENCES nexora.payroll_period (id, company_id),
    CONSTRAINT payroll_variable_amount_chk CHECK (amount > 0),
    CONSTRAINT payroll_variable_status_chk CHECK (status IN ('submitted', 'approved', 'rejected', 'cancelled')),
    CONSTRAINT payroll_variable_four_eyes_chk CHECK (decided_by IS NULL OR decided_by <> submitted_by),
    CONSTRAINT payroll_variable_decided_chk CHECK (
        status NOT IN ('approved', 'rejected') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CONSTRAINT payroll_variable_rejected_chk CHECK (status <> 'rejected' OR length(btrim(coalesce(decision_note, ''))) > 0)
);
CREATE INDEX payroll_variable_period_idx ON nexora.payroll_variable (period_id, status);
CREATE INDEX payroll_variable_employee_idx ON nexora.payroll_variable (employee_id);

-- =====================================================================
-- 7. Règles légales et sociales configurables, versionnées (§11, §31)
-- =====================================================================
CREATE TABLE nexora.payroll_rule (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      uuid REFERENCES nexora.company(id),        -- NULL = modèle à adapter et valider
    code            text NOT NULL,
    name            text NOT NULL,
    jurisdiction    text NOT NULL DEFAULT 'HT',
    rule_type       text NOT NULL,
    base_type       text NOT NULL DEFAULT 'taxable',
    method          text NOT NULL,
    rate            numeric(9,6),                               -- %
    fixed_amount    nexora.money_amount,
    brackets        jsonb NOT NULL DEFAULT '[]'::jsonb,         -- [{"from":0,"to":60000,"rate":0}, …]
    annualize       boolean NOT NULL DEFAULT false,             -- barème annuel appliqué sur la base annualisée
    base_floor      nexora.money_amount,
    base_ceiling    nexora.money_amount,
    min_amount      nexora.money_amount,
    max_amount      nexora.money_amount,
    valid_from      date NOT NULL,
    valid_to        date,
    version         integer NOT NULL DEFAULT 1,
    status          text NOT NULL DEFAULT 'draft',
    legal_reference text,
    notes           text,
    supersedes_id   uuid REFERENCES nexora.payroll_rule(id),
    validated_by    uuid REFERENCES nexora.app_user(id),
    validated_at    timestamptz,
    validation_note text,
    created_by      uuid REFERENCES nexora.app_user(id),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payroll_rule_version_uk UNIQUE NULLS NOT DISTINCT (company_id, code, version),
    CONSTRAINT payroll_rule_type_chk CHECK (rule_type IN ('employee_contribution', 'employer_contribution', 'income_tax')),
    CONSTRAINT payroll_rule_base_chk CHECK (base_type IN ('taxable', 'social', 'gross', 'taxable_after_social')),
    CONSTRAINT payroll_rule_method_chk CHECK (method IN ('percentage', 'fixed', 'progressive')),
    CONSTRAINT payroll_rule_values_chk CHECK (
        (method = 'percentage' AND rate IS NOT NULL AND rate >= 0 AND rate <= 100)
     OR (method = 'fixed' AND fixed_amount IS NOT NULL AND fixed_amount >= 0)
     OR (method = 'progressive' AND jsonb_typeof(brackets) = 'array' AND jsonb_array_length(brackets) > 0)),
    CONSTRAINT payroll_rule_dates_chk CHECK (valid_to IS NULL OR valid_to >= valid_from),
    CONSTRAINT payroll_rule_status_chk CHECK (status IN ('template', 'draft', 'validated', 'retired')),
    CONSTRAINT payroll_rule_template_chk CHECK ((status = 'template') = (company_id IS NULL)),
    CONSTRAINT payroll_rule_validated_chk CHECK (
        status NOT IN ('validated', 'retired') OR (validated_by IS NOT NULL AND validated_at IS NOT NULL))
);
CREATE INDEX payroll_rule_company_idx ON nexora.payroll_rule (company_id, status, valid_from);

-- Règle validée = figée : seuls la date de fin et le retrait sont possibles ; toute autre évolution crée
-- une nouvelle version (§31 « versionner les règles de calcul »).
CREATE OR REPLACE FUNCTION nexora.payroll_rule_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status IN ('draft', 'template') OR nexora.flag('allow_hard_delete') THEN RETURN OLD; END IF;
        RAISE EXCEPTION 'Règle % v% validée : suppression interdite (la retirer)', OLD.code, OLD.version
            USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status IN ('validated', 'retired')
       AND (to_jsonb(NEW) - ARRAY['status', 'valid_to', 'updated_at'])
           IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status', 'valid_to', 'updated_at']) THEN
        RAISE EXCEPTION 'Règle % v% validée : paramètres figés — créer une nouvelle version', OLD.code, OLD.version
            USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status = 'retired' AND NEW.status <> 'retired' THEN
        RAISE EXCEPTION 'Règle % v% retirée : réactivation interdite', OLD.code, OLD.version USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_payroll_rule_guard BEFORE UPDATE OR DELETE ON nexora.payroll_rule
    FOR EACH ROW EXECUTE FUNCTION nexora.payroll_rule_guard();

-- =====================================================================
-- 8. Avances, acomptes et prêts au personnel (§12-§17)
-- =====================================================================
CREATE TABLE nexora.employee_advance (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid NOT NULL REFERENCES nexora.company(id),
    advance_number       text NOT NULL,
    employee_id          uuid NOT NULL,
    kind                 text NOT NULL DEFAULT 'salary_advance',
    currency_id          uuid NOT NULL REFERENCES nexora.currency(id),
    request_date         date NOT NULL,
    amount_requested     nexora.money_amount NOT NULL,
    amount_approved      nexora.money_amount,
    amount_paid          nexora.money_amount NOT NULL DEFAULT 0,
    recovered_amount     nexora.money_amount NOT NULL DEFAULT 0,
    balance              nexora.money_amount GENERATED ALWAYS AS (amount_paid - recovered_amount) STORED,
    reason               text,
    conditions           text,                                   -- prêt : conditions propres (§17)
    recovery_method      text NOT NULL DEFAULT 'next_payroll',
    installments_count   integer NOT NULL DEFAULT 1,
    recovery_percent     numeric(9,4),
    first_recovery_date  date,
    status               text NOT NULL DEFAULT 'requested',
    approval_request_id  uuid,                                   -- workflow configuré (Phase 11)
    requested_by         uuid NOT NULL REFERENCES nexora.app_user(id),
    requested_at         timestamptz NOT NULL DEFAULT now(),
    approved_by          uuid REFERENCES nexora.app_user(id),
    approved_at          timestamptz,
    decision_note        text,
    paid_by              uuid REFERENCES nexora.app_user(id),
    paid_at              timestamptz,
    financial_account_id uuid REFERENCES nexora.financial_account(id),
    payment_method       text,
    payment_reference    text,
    cash_session_id      uuid,
    journal_entry_id     uuid,
    payment_operation_uuid uuid,
    settled_at           timestamptz,
    cancelled_by         uuid REFERENCES nexora.app_user(id),
    cancelled_at         timestamptz,
    cancel_reason        text,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT employee_advance_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT employee_advance_number_uk UNIQUE (company_id, advance_number),
    CONSTRAINT employee_advance_payment_op_uk UNIQUE (payment_operation_uuid),
    CONSTRAINT employee_advance_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT employee_advance_session_fk FOREIGN KEY (cash_session_id, company_id)
        REFERENCES nexora.cash_session (id, company_id),
    CONSTRAINT employee_advance_kind_chk CHECK (kind IN ('salary_advance', 'acompte', 'loan')),
    CONSTRAINT employee_advance_requested_chk CHECK (amount_requested > 0),
    CONSTRAINT employee_advance_approved_chk CHECK (
        amount_approved IS NULL OR (amount_approved > 0 AND amount_approved <= amount_requested)),
    CONSTRAINT employee_advance_paid_chk CHECK (amount_paid >= 0 AND amount_paid <= coalesce(amount_approved, 0)),
    -- §15 : jamais de récupération au-delà du montant versé.
    CONSTRAINT employee_advance_recovered_chk CHECK (recovered_amount >= 0 AND recovered_amount <= amount_paid),
    CONSTRAINT employee_advance_method_chk
        CHECK (recovery_method IN ('next_payroll', 'installments', 'percent_of_net', 'manual')),
    CONSTRAINT employee_advance_installments_chk CHECK (installments_count BETWEEN 1 AND 120),
    CONSTRAINT employee_advance_percent_chk CHECK (
        recovery_method <> 'percent_of_net' OR (recovery_percent IS NOT NULL AND recovery_percent > 0 AND recovery_percent <= 100)),
    CONSTRAINT employee_advance_payment_method_chk CHECK (
        payment_method IS NULL OR payment_method IN ('cash', 'bank_transfer', 'mobile_money', 'check', 'other')),
    CONSTRAINT employee_advance_status_chk CHECK (status IN (
        'requested', 'approved', 'paid', 'in_repayment', 'settled', 'rejected', 'cancelled')),
    CONSTRAINT employee_advance_four_eyes_chk CHECK (approved_by IS NULL OR approved_by <> requested_by),
    CONSTRAINT employee_advance_approval_chk CHECK (
        status NOT IN ('approved', 'paid', 'in_repayment', 'settled')
        OR (approved_by IS NOT NULL AND approved_at IS NOT NULL AND amount_approved IS NOT NULL)),
    CONSTRAINT employee_advance_payment_chk CHECK (
        status NOT IN ('paid', 'in_repayment', 'settled')
        OR (paid_at IS NOT NULL AND amount_paid > 0 AND financial_account_id IS NOT NULL AND journal_entry_id IS NOT NULL)),
    CONSTRAINT employee_advance_settled_chk CHECK (status <> 'settled' OR recovered_amount = amount_paid),
    CONSTRAINT employee_advance_rejected_chk CHECK (status <> 'rejected' OR length(btrim(coalesce(decision_note, ''))) > 0),
    CONSTRAINT employee_advance_cancel_chk CHECK (
        status <> 'cancelled' OR (cancelled_at IS NOT NULL AND length(btrim(coalesce(cancel_reason, ''))) > 0))
);
CREATE INDEX employee_advance_employee_idx ON nexora.employee_advance (employee_id, status);
CREATE INDEX employee_advance_status_idx ON nexora.employee_advance (company_id, status);

-- L'approbateur n'est jamais l'employé bénéficiaire lui-même (§39 « s'auto-approuver »).
CREATE OR REPLACE FUNCTION nexora.employee_advance_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_employee_user uuid;
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF nexora.flag('allow_hard_delete') THEN RETURN OLD; END IF;
        RAISE EXCEPTION 'Avance % : suppression physique interdite', OLD.advance_number USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.approved_by IS NOT NULL AND (TG_OP = 'INSERT' OR OLD.approved_by IS DISTINCT FROM NEW.approved_by) THEN
        SELECT user_id INTO v_employee_user FROM nexora.employee WHERE id = NEW.employee_id;
        IF v_employee_user IS NOT NULL AND v_employee_user = NEW.approved_by THEN
            RAISE EXCEPTION 'Avance % : le bénéficiaire ne peut pas approuver sa propre avance', NEW.advance_number
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.amount_paid > 0 AND (NEW.amount_paid <> OLD.amount_paid OR NEW.amount_approved IS DISTINCT FROM OLD.amount_approved
                                    OR NEW.currency_id <> OLD.currency_id OR NEW.employee_id <> OLD.employee_id) THEN
            RAISE EXCEPTION 'Avance % versée : montants figés', OLD.advance_number USING ERRCODE = 'check_violation';
        END IF;
        IF OLD.status IN ('settled', 'rejected', 'cancelled') AND NEW.status <> OLD.status THEN
            RAISE EXCEPTION 'Avance % (%) : statut final', OLD.advance_number, OLD.status USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_employee_advance_guard BEFORE INSERT OR UPDATE OR DELETE ON nexora.employee_advance
    FOR EACH ROW EXECUTE FUNCTION nexora.employee_advance_guard();

CREATE TABLE nexora.advance_installment (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id        uuid NOT NULL REFERENCES nexora.company(id),
    advance_id        uuid NOT NULL,
    seq_no            integer NOT NULL,
    schedule_version  integer NOT NULL DEFAULT 1,
    due_date          date NOT NULL,                             -- récupérée par la paie couvrant cette date
    amount            nexora.money_amount NOT NULL,
    recovered_amount  nexora.money_amount NOT NULL DEFAULT 0,
    status            text NOT NULL DEFAULT 'pending',
    created_by        uuid REFERENCES nexora.app_user(id),
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT advance_installment_advance_fk FOREIGN KEY (advance_id, company_id)
        REFERENCES nexora.employee_advance (id, company_id),
    CONSTRAINT advance_installment_uk UNIQUE (advance_id, schedule_version, seq_no),
    CONSTRAINT advance_installment_amount_chk CHECK (amount > 0),
    CONSTRAINT advance_installment_recovered_chk CHECK (recovered_amount >= 0 AND recovered_amount <= amount),
    CONSTRAINT advance_installment_status_chk CHECK (status IN ('pending', 'partial', 'recovered', 'replaced'))
);
CREATE INDEX advance_installment_due_idx ON nexora.advance_installment (advance_id, status, due_date);
CREATE TRIGGER trg_advance_installment_forbid_delete BEFORE DELETE ON nexora.advance_installment
    FOR EACH ROW EXECUTE FUNCTION nexora.hr_forbid_delete();

CREATE TABLE nexora.advance_repayment (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid NOT NULL REFERENCES nexora.company(id),
    advance_id            uuid NOT NULL,
    source                text NOT NULL,                          -- payroll | manual
    payroll_item_id       uuid,
    payroll_deduction_id  uuid,
    amount                nexora.money_amount NOT NULL,
    currency_id           uuid NOT NULL REFERENCES nexora.currency(id),
    repaid_at             timestamptz NOT NULL DEFAULT now(),
    financial_account_id  uuid REFERENCES nexora.financial_account(id),
    payment_method        text,
    reference             text,
    journal_entry_id      uuid,
    operation_uuid        uuid,
    note                  text,
    created_by            uuid REFERENCES nexora.app_user(id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT advance_repayment_advance_fk FOREIGN KEY (advance_id, company_id)
        REFERENCES nexora.employee_advance (id, company_id),
    CONSTRAINT advance_repayment_item_fk FOREIGN KEY (payroll_item_id, company_id)
        REFERENCES nexora.payroll_item (id, company_id),
    CONSTRAINT advance_repayment_deduction_fk FOREIGN KEY (payroll_deduction_id, company_id)
        REFERENCES nexora.payroll_deduction (id, company_id),
    CONSTRAINT advance_repayment_amount_chk CHECK (amount > 0),
    CONSTRAINT advance_repayment_source_chk CHECK (
        (source = 'payroll' AND payroll_item_id IS NOT NULL AND payroll_deduction_id IS NOT NULL)
     OR (source = 'manual' AND financial_account_id IS NOT NULL AND operation_uuid IS NOT NULL)),
    CONSTRAINT advance_repayment_operation_uk UNIQUE (operation_uuid),
    CONSTRAINT advance_repayment_deduction_uk UNIQUE (payroll_deduction_id)
);
CREATE INDEX advance_repayment_advance_idx ON nexora.advance_repayment (advance_id, repaid_at);
CREATE TRIGGER trg_advance_repayment_immutable BEFORE UPDATE OR DELETE ON nexora.advance_repayment
    FOR EACH ROW EXECUTE FUNCTION nexora.hr_immutable_row();

-- Chaque récupération diminue le solde (§13.8) ; au-delà du solde, refus (§15, §37 test 22) ;
-- solde à zéro → « soldée » (§13.9). Les échéances sont imputées de la plus ancienne à la plus récente.
CREATE OR REPLACE FUNCTION nexora.advance_repayment_apply()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    adv   record;
    inst  record;
    left_ nexora.money_amount := NEW.amount;
    part  nexora.money_amount;
BEGIN
    SELECT id, advance_number, status, currency_id, amount_paid, recovered_amount
      INTO adv FROM nexora.employee_advance WHERE id = NEW.advance_id FOR UPDATE;
    IF adv.status NOT IN ('paid', 'in_repayment') THEN
        RAISE EXCEPTION 'Avance % (%) : aucune récupération possible', adv.advance_number, adv.status
            USING ERRCODE = 'check_violation';
    END IF;
    IF adv.currency_id <> NEW.currency_id THEN
        RAISE EXCEPTION 'Avance % : devise de la récupération différente de celle de l''avance', adv.advance_number
            USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.amount > adv.amount_paid - adv.recovered_amount THEN
        RAISE EXCEPTION 'Avance % : récupération % supérieure au solde restant %', adv.advance_number, NEW.amount,
            adv.amount_paid - adv.recovered_amount USING ERRCODE = 'check_violation';
    END IF;

    UPDATE nexora.employee_advance
       SET recovered_amount = recovered_amount + NEW.amount,
           status = CASE WHEN recovered_amount + NEW.amount = amount_paid THEN 'settled' ELSE 'in_repayment' END,
           settled_at = CASE WHEN recovered_amount + NEW.amount = amount_paid THEN now() ELSE settled_at END
     WHERE id = NEW.advance_id;

    FOR inst IN
        SELECT id, amount - recovered_amount AS open_amount FROM nexora.advance_installment
         WHERE advance_id = NEW.advance_id AND status IN ('pending', 'partial')
         ORDER BY due_date, seq_no
         FOR UPDATE
    LOOP
        EXIT WHEN left_ <= 0;
        part := least(left_, inst.open_amount);
        UPDATE nexora.advance_installment
           SET recovered_amount = recovered_amount + part,
               status = CASE WHEN recovered_amount + part = amount THEN 'recovered' ELSE 'partial' END
         WHERE id = inst.id;
        left_ := left_ - part;
    END LOOP;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_advance_repayment_apply AFTER INSERT ON nexora.advance_repayment
    FOR EACH ROW EXECUTE FUNCTION nexora.advance_repayment_apply();

-- Journal des étapes d'une avance (§13.10 « toutes les étapes restent dans l'audit », §29 historique).
CREATE TABLE nexora.employee_advance_event (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id   uuid NOT NULL REFERENCES nexora.company(id),
    advance_id   uuid NOT NULL,
    event_type   text NOT NULL,
    amount       nexora.money_amount,
    balance_after nexora.money_amount,
    reference    text,
    note         text,
    actor_id     uuid REFERENCES nexora.app_user(id),
    occurred_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT employee_advance_event_advance_fk FOREIGN KEY (advance_id, company_id)
        REFERENCES nexora.employee_advance (id, company_id),
    CONSTRAINT employee_advance_event_type_chk CHECK (event_type IN (
        'requested', 'approval_step', 'approved', 'rejected', 'paid', 'schedule_set', 'schedule_changed',
        'recovered_payroll', 'recovered_manual', 'settled', 'cancelled'))
);
CREATE INDEX employee_advance_event_idx ON nexora.employee_advance_event (advance_id, occurred_at);
CREATE TRIGGER trg_employee_advance_event_immutable BEFORE UPDATE OR DELETE ON nexora.employee_advance_event
    FOR EACH ROW EXECUTE FUNCTION nexora.hr_immutable_row();

-- =====================================================================
-- 9. Remboursements de frais — dette de l'entreprise envers l'employé (§17, §32)
-- =====================================================================
CREATE TABLE nexora.employee_expense_claim (
    id                        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id                uuid NOT NULL REFERENCES nexora.company(id),
    claim_number              text NOT NULL,
    employee_id               uuid NOT NULL,
    expense_date              date NOT NULL,
    category                  text NOT NULL,
    description               text NOT NULL,
    amount                    nexora.money_amount NOT NULL,
    currency_id               uuid NOT NULL REFERENCES nexora.currency(id),
    receipt_document_id       uuid,
    settlement                text NOT NULL DEFAULT 'payroll',     -- payroll | direct
    status                    text NOT NULL DEFAULT 'submitted',
    submitted_by              uuid NOT NULL REFERENCES nexora.app_user(id),
    submitted_at              timestamptz NOT NULL DEFAULT now(),
    decided_by                uuid REFERENCES nexora.app_user(id),
    decided_at                timestamptz,
    decision_note             text,
    expense_account_id        uuid REFERENCES nexora.account(id),
    approval_journal_entry_id uuid,
    approval_operation_uuid   uuid,
    payroll_item_id           uuid,
    paid_by                   uuid REFERENCES nexora.app_user(id),
    paid_at                   timestamptz,
    financial_account_id      uuid REFERENCES nexora.financial_account(id),
    payment_reference         text,
    payment_journal_entry_id  uuid,
    payment_operation_uuid    uuid,
    cancelled_at              timestamptz,
    cancel_reason             text,
    created_at                timestamptz NOT NULL DEFAULT now(),
    updated_at                timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT employee_expense_claim_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT employee_expense_claim_number_uk UNIQUE (company_id, claim_number),
    CONSTRAINT employee_expense_claim_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT employee_expense_claim_amount_chk CHECK (amount > 0),
    CONSTRAINT employee_expense_claim_category_chk CHECK (category IN (
        'transport', 'meal', 'lodging', 'supplies', 'communication', 'fuel', 'other')),
    CONSTRAINT employee_expense_claim_settlement_chk CHECK (settlement IN ('payroll', 'direct')),
    CONSTRAINT employee_expense_claim_status_chk CHECK (status IN (
        'submitted', 'approved', 'rejected', 'in_payroll', 'paid', 'cancelled')),
    CONSTRAINT employee_expense_claim_four_eyes_chk CHECK (decided_by IS NULL OR decided_by <> submitted_by),
    CONSTRAINT employee_expense_claim_approved_chk CHECK (
        status NOT IN ('approved', 'in_payroll', 'paid') OR (decided_by IS NOT NULL AND approval_journal_entry_id IS NOT NULL)),
    CONSTRAINT employee_expense_claim_paid_chk CHECK (
        status <> 'paid' OR (paid_at IS NOT NULL AND (payroll_item_id IS NOT NULL OR payment_journal_entry_id IS NOT NULL))),
    CONSTRAINT employee_expense_claim_rejected_chk CHECK (status <> 'rejected' OR length(btrim(coalesce(decision_note, ''))) > 0),
    CONSTRAINT employee_expense_claim_ops_uk UNIQUE (payment_operation_uuid),
    CONSTRAINT employee_expense_claim_approval_op_uk UNIQUE (approval_operation_uuid)
);
CREATE INDEX employee_expense_claim_employee_idx ON nexora.employee_expense_claim (employee_id, status);
CREATE TRIGGER trg_employee_expense_claim_forbid_delete BEFORE DELETE ON nexora.employee_expense_claim
    FOR EACH ROW EXECUTE FUNCTION nexora.hr_forbid_delete();

-- =====================================================================
-- 10. Paiements des salaires & bulletins (§24, §25)
-- =====================================================================
CREATE TABLE nexora.salary_payment (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id            uuid NOT NULL REFERENCES nexora.company(id),
    payment_number        text NOT NULL,
    payroll_item_id       uuid NOT NULL,
    run_id                uuid NOT NULL,
    employee_id           uuid NOT NULL,
    amount                nexora.money_amount NOT NULL,
    currency_id           uuid NOT NULL REFERENCES nexora.currency(id),
    payment_method        text NOT NULL,
    financial_account_id  uuid NOT NULL REFERENCES nexora.financial_account(id),
    cash_session_id       uuid,
    reference             text,
    beneficiary_account   text,                                   -- compte bénéficiaire masqué (••••1234)
    paid_at               timestamptz NOT NULL,
    status                text NOT NULL DEFAULT 'completed',
    journal_entry_id      uuid NOT NULL,
    operation_uuid        uuid NOT NULL,
    created_by            uuid REFERENCES nexora.app_user(id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    cancelled_by          uuid REFERENCES nexora.app_user(id),
    cancelled_at          timestamptz,
    cancel_reason         text,
    reversal_journal_entry_id uuid,
    CONSTRAINT salary_payment_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT salary_payment_number_uk UNIQUE (company_id, payment_number),
    CONSTRAINT salary_payment_operation_uk UNIQUE (operation_uuid),
    CONSTRAINT salary_payment_item_fk FOREIGN KEY (payroll_item_id, company_id)
        REFERENCES nexora.payroll_item (id, company_id),
    CONSTRAINT salary_payment_run_fk FOREIGN KEY (run_id, company_id) REFERENCES nexora.payroll_run (id, company_id),
    CONSTRAINT salary_payment_employee_fk FOREIGN KEY (employee_id, company_id) REFERENCES nexora.employee (id, company_id),
    CONSTRAINT salary_payment_session_fk FOREIGN KEY (cash_session_id, company_id)
        REFERENCES nexora.cash_session (id, company_id),
    CONSTRAINT salary_payment_amount_chk CHECK (amount > 0),
    CONSTRAINT salary_payment_method_chk
        CHECK (payment_method IN ('bank_transfer', 'cash', 'mobile_money', 'check', 'other')),
    CONSTRAINT salary_payment_status_chk CHECK (status IN ('completed', 'cancelled')),
    CONSTRAINT salary_payment_cancel_chk CHECK (
        status <> 'cancelled' OR (cancelled_at IS NOT NULL AND reversal_journal_entry_id IS NOT NULL
                                  AND length(btrim(coalesce(cancel_reason, ''))) > 0))
);
-- §39 « ne jamais créer deux paiements pour une même instruction » : un seul paiement actif par ligne de paie.
CREATE UNIQUE INDEX salary_payment_item_active_uk ON nexora.salary_payment (payroll_item_id) WHERE status = 'completed';
CREATE INDEX salary_payment_employee_idx ON nexora.salary_payment (employee_id, paid_at DESC);
CREATE INDEX salary_payment_run_idx ON nexora.salary_payment (run_id);

-- Paiement : seul le passage à « annulé » (extourne) est possible ; jamais supprimé.
CREATE OR REPLACE FUNCTION nexora.salary_payment_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF nexora.flag('allow_hard_delete') THEN RETURN OLD; END IF;
        RAISE EXCEPTION 'Paiement de salaire % : suppression interdite', OLD.payment_number USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status = 'cancelled'
       OR (to_jsonb(NEW) - ARRAY['status', 'cancelled_by', 'cancelled_at', 'cancel_reason', 'reversal_journal_entry_id'])
          IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status', 'cancelled_by', 'cancelled_at', 'cancel_reason', 'reversal_journal_entry_id']) THEN
        RAISE EXCEPTION 'Paiement de salaire % : seul son annulation (avec extourne) est possible', OLD.payment_number
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_salary_payment_guard BEFORE UPDATE OR DELETE ON nexora.salary_payment
    FOR EACH ROW EXECUTE FUNCTION nexora.salary_payment_guard();

CREATE TABLE nexora.payslip (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid NOT NULL REFERENCES nexora.company(id),
    payslip_number     text NOT NULL,                             -- identifiant unique (§25)
    payroll_item_id    uuid NOT NULL,
    run_id             uuid NOT NULL,
    period_id          uuid NOT NULL,
    employee_id        uuid NOT NULL,
    salary_payment_id  uuid,
    snapshot           jsonb NOT NULL,                             -- bulletin figé (données imprimées)
    sha256_hash        text NOT NULL,
    status             text NOT NULL DEFAULT 'issued',
    issued_by          uuid REFERENCES nexora.app_user(id),
    issued_at          timestamptz NOT NULL DEFAULT now(),
    cancelled_at       timestamptz,
    cancel_reason      text,
    CONSTRAINT payslip_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT payslip_number_uk UNIQUE (company_id, payslip_number),
    CONSTRAINT payslip_item_fk FOREIGN KEY (payroll_item_id, company_id) REFERENCES nexora.payroll_item (id, company_id),
    CONSTRAINT payslip_run_fk FOREIGN KEY (run_id, company_id) REFERENCES nexora.payroll_run (id, company_id),
    CONSTRAINT payslip_employee_fk FOREIGN KEY (employee_id, company_id) REFERENCES nexora.employee (id, company_id),
    CONSTRAINT payslip_payment_fk FOREIGN KEY (salary_payment_id, company_id)
        REFERENCES nexora.salary_payment (id, company_id),
    CONSTRAINT payslip_status_chk CHECK (status IN ('issued', 'cancelled')),
    CONSTRAINT payslip_cancel_chk CHECK (
        status <> 'cancelled' OR (cancelled_at IS NOT NULL AND length(btrim(coalesce(cancel_reason, ''))) > 0))
);
CREATE UNIQUE INDEX payslip_item_active_uk ON nexora.payslip (payroll_item_id) WHERE status = 'issued';
CREATE INDEX payslip_employee_idx ON nexora.payslip (employee_id, issued_at DESC);

CREATE OR REPLACE FUNCTION nexora.payslip_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF nexora.flag('allow_hard_delete') THEN RETURN OLD; END IF;
        RAISE EXCEPTION 'Bulletin % : suppression interdite', OLD.payslip_number USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status = 'cancelled'
       OR (to_jsonb(NEW) - ARRAY['status', 'cancelled_at', 'cancel_reason'])
          IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status', 'cancelled_at', 'cancel_reason']) THEN
        RAISE EXCEPTION 'Bulletin % figé : seul son annulation est possible', OLD.payslip_number USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_payslip_guard BEFORE UPDATE OR DELETE ON nexora.payslip
    FOR EACH ROW EXECUTE FUNCTION nexora.payslip_guard();

-- =====================================================================
-- 11. Régularisations reliées à la paie d'origine (§36)
-- =====================================================================
CREATE TABLE nexora.payroll_adjustment (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id         uuid NOT NULL REFERENCES nexora.company(id),
    adjustment_number  text NOT NULL,
    employee_id        uuid NOT NULL,
    original_run_id    uuid NOT NULL,
    original_item_id   uuid NOT NULL,
    kind               text NOT NULL,                             -- earning (rappel) | deduction (trop-perçu)
    component_id       uuid REFERENCES nexora.pay_component(id),
    amount             nexora.money_amount NOT NULL,
    taxable            boolean NOT NULL DEFAULT true,
    reason             text NOT NULL,
    status             text NOT NULL DEFAULT 'requested',
    requested_by       uuid NOT NULL REFERENCES nexora.app_user(id),
    requested_at       timestamptz NOT NULL DEFAULT now(),
    decided_by         uuid REFERENCES nexora.app_user(id),
    decided_at         timestamptz,
    decision_note      text,
    applied_item_id    uuid,
    applied_at         timestamptz,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payroll_adjustment_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT payroll_adjustment_number_uk UNIQUE (company_id, adjustment_number),
    CONSTRAINT payroll_adjustment_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT payroll_adjustment_run_fk FOREIGN KEY (original_run_id, company_id)
        REFERENCES nexora.payroll_run (id, company_id),
    CONSTRAINT payroll_adjustment_item_fk FOREIGN KEY (original_item_id, company_id)
        REFERENCES nexora.payroll_item (id, company_id),
    CONSTRAINT payroll_adjustment_kind_chk CHECK (kind IN ('earning', 'deduction')),
    CONSTRAINT payroll_adjustment_amount_chk CHECK (amount > 0),
    CONSTRAINT payroll_adjustment_reason_chk CHECK (length(btrim(reason)) >= 5),
    CONSTRAINT payroll_adjustment_status_chk CHECK (status IN ('requested', 'approved', 'rejected', 'applied', 'cancelled')),
    CONSTRAINT payroll_adjustment_four_eyes_chk CHECK (decided_by IS NULL OR decided_by <> requested_by),
    CONSTRAINT payroll_adjustment_decided_chk CHECK (
        status NOT IN ('approved', 'rejected', 'applied') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)),
    CONSTRAINT payroll_adjustment_applied_chk CHECK (status <> 'applied' OR applied_item_id IS NOT NULL),
    CONSTRAINT payroll_adjustment_rejected_chk CHECK (status <> 'rejected' OR length(btrim(coalesce(decision_note, ''))) > 0)
);
CREATE INDEX payroll_adjustment_employee_idx ON nexora.payroll_adjustment (employee_id, status);
CREATE TRIGGER trg_payroll_adjustment_forbid_delete BEFORE DELETE ON nexora.payroll_adjustment
    FOR EACH ROW EXECUTE FUNCTION nexora.hr_forbid_delete();

-- =====================================================================
-- 12. Documents RH confidentiels (§7 « Documents ») — hors GED : accès réservé aux droits paie/RH
-- =====================================================================
CREATE TABLE nexora.employee_document (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id           uuid NOT NULL REFERENCES nexora.company(id),
    employee_id          uuid NOT NULL,
    document_type        text NOT NULL,
    title                text NOT NULL,
    file_name            text NOT NULL,
    content              bytea NOT NULL,
    content_type         text NOT NULL,
    size_bytes           integer NOT NULL,
    sha256_hash          text NOT NULL,
    visible_to_employee  boolean NOT NULL DEFAULT false,
    supersedes_id        uuid REFERENCES nexora.employee_document(id),
    archived_at          timestamptz,
    archived_by          uuid REFERENCES nexora.app_user(id),
    uploaded_by          uuid REFERENCES nexora.app_user(id),
    created_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT employee_document_company_id_uk UNIQUE (id, company_id),
    CONSTRAINT employee_document_employee_fk FOREIGN KEY (employee_id, company_id)
        REFERENCES nexora.employee (id, company_id),
    CONSTRAINT employee_document_type_chk CHECK (document_type IN (
        'contract', 'id_document', 'diploma', 'certificate', 'medical', 'expense_receipt', 'disciplinary', 'other')),
    CONSTRAINT employee_document_size_chk CHECK (size_bytes > 0 AND size_bytes <= 10485760)   -- 10 Mo
);
CREATE INDEX employee_document_employee_idx ON nexora.employee_document (employee_id, created_at DESC);

ALTER TABLE nexora.employee_expense_claim
    ADD CONSTRAINT employee_expense_claim_receipt_fk FOREIGN KEY (receipt_document_id, company_id)
        REFERENCES nexora.employee_document (id, company_id);

-- Contenu jamais modifié ; seul l'archivage est possible ; jamais supprimé.
CREATE OR REPLACE FUNCTION nexora.employee_document_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF nexora.flag('allow_hard_delete') THEN RETURN OLD; END IF;
        RAISE EXCEPTION 'Document RH % : suppression interdite (l''archiver)', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF (to_jsonb(NEW) - ARRAY['archived_at', 'archived_by', 'visible_to_employee'])
       IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['archived_at', 'archived_by', 'visible_to_employee']) THEN
        RAISE EXCEPTION 'Document RH % : contenu figé (déposer une nouvelle version)', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_employee_document_guard BEFORE UPDATE OR DELETE ON nexora.employee_document
    FOR EACH ROW EXECUTE FUNCTION nexora.employee_document_guard();

-- Audit du document sans son contenu binaire (le journal d'audit ne duplique pas le fichier).
CREATE OR REPLACE FUNCTION nexora.employee_document_audit()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO nexora.audit_log (company_id, user_id, action, entity_type, entity_id, old_values, new_values, context)
    VALUES (coalesce(NEW.company_id, OLD.company_id), nexora.current_user_id(), TG_OP, 'employee_document',
            coalesce(NEW.id, OLD.id),
            CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE to_jsonb(OLD) - 'content' END,
            CASE WHEN TG_OP = 'DELETE' THEN NULL ELSE to_jsonb(NEW) - 'content' END,
            jsonb_build_object('txid', txid_current(), 'at', now()));
    RETURN coalesce(NEW, OLD);
END;
$$;
CREATE TRIGGER trg_audit_employee_document AFTER INSERT OR UPDATE ON nexora.employee_document
    FOR EACH ROW EXECUTE FUNCTION nexora.employee_document_audit();

-- =====================================================================
-- 13. Verrouillage des données de temps et variables (§18 « verrouiller après clôture », §22)
-- Une donnée tombant dans une paie VALIDÉE (ou au-delà) de l'employé est figée ; dans une paie calculée
-- mais pas encore validée, la modification marque la paie « à recalculer » (jamais silencieusement ignorée).
-- =====================================================================
CREATE OR REPLACE FUNCTION nexora.payroll_time_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_employee uuid := coalesce(NEW.employee_id, OLD.employee_id);
    v_from     date;
    v_to       date;
    v_locked   text;
BEGIN
    IF TG_TABLE_NAME = 'payroll_variable' THEN
        SELECT p.start_date, p.end_date INTO v_from, v_to FROM nexora.payroll_period p
         WHERE p.id = coalesce(NEW.period_id, OLD.period_id);
    ELSIF TG_TABLE_NAME = 'leave_record' THEN
        v_from := least(coalesce(NEW.start_date, OLD.start_date), coalesce(OLD.start_date, NEW.start_date));
        v_to   := greatest(coalesce(NEW.end_date, OLD.end_date), coalesce(OLD.end_date, NEW.end_date));
    ELSE
        v_from := least(coalesce(NEW.work_date, OLD.work_date), coalesce(OLD.work_date, NEW.work_date));
        v_to   := greatest(coalesce(NEW.work_date, OLD.work_date), coalesce(OLD.work_date, NEW.work_date));
    END IF;

    -- Seule exception : l'intégration de la ligne dans la paie qui l'a calculée (payroll_item_id posé).
    IF TG_OP = 'UPDATE' AND TG_TABLE_NAME IN ('overtime_record', 'payroll_variable')
       AND (to_jsonb(NEW) - ARRAY['payroll_item_id', 'updated_at']) = (to_jsonb(OLD) - ARRAY['payroll_item_id', 'updated_at']) THEN
        RETURN NEW;
    END IF;

    SELECT r.run_number INTO v_locked
      FROM nexora.payroll_item i
      JOIN nexora.payroll_run r ON r.id = i.run_id
      JOIN nexora.payroll_period p ON p.id = i.period_id
     WHERE i.employee_id = v_employee
       AND i.status <> 'excluded'
       AND r.status IN ('validated', 'approved', 'paid', 'closed')
       AND daterange(p.start_date, p.end_date, '[]') && daterange(v_from, v_to, '[]')
     LIMIT 1;
    IF v_locked IS NOT NULL THEN
        RAISE EXCEPTION 'Période verrouillée par la paie % : correction par régularisation uniquement', v_locked
            USING ERRCODE = 'check_violation';
    END IF;

    UPDATE nexora.payroll_run r SET stale = true
      FROM nexora.payroll_item i, nexora.payroll_period p
     WHERE i.run_id = r.id AND p.id = i.period_id AND i.employee_id = v_employee
       AND r.status IN ('calculated', 'reviewed')
       AND daterange(p.start_date, p.end_date, '[]') && daterange(v_from, v_to, '[]');
    RETURN coalesce(NEW, OLD);
END;
$$;
CREATE TRIGGER trg_payroll_time_guard BEFORE INSERT OR UPDATE OR DELETE ON nexora.attendance_record
    FOR EACH ROW EXECUTE FUNCTION nexora.payroll_time_guard();
CREATE TRIGGER trg_payroll_time_guard BEFORE INSERT OR UPDATE OR DELETE ON nexora.leave_record
    FOR EACH ROW EXECUTE FUNCTION nexora.payroll_time_guard();
CREATE TRIGGER trg_payroll_time_guard BEFORE INSERT OR UPDATE OR DELETE ON nexora.overtime_record
    FOR EACH ROW EXECUTE FUNCTION nexora.payroll_time_guard();
CREATE TRIGGER trg_payroll_time_guard BEFORE INSERT OR UPDATE OR DELETE ON nexora.payroll_variable
    FOR EACH ROW EXECUTE FUNCTION nexora.payroll_time_guard();

-- Un changement de salaire daté dans une paie validée est refusé : rétroactivité = régularisation (§31).
CREATE OR REPLACE FUNCTION nexora.salary_history_period_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_locked text;
BEGIN
    SELECT r.run_number INTO v_locked
      FROM nexora.payroll_item i
      JOIN nexora.payroll_run r ON r.id = i.run_id
      JOIN nexora.payroll_period p ON p.id = i.period_id
     WHERE i.employee_id = NEW.employee_id AND i.status <> 'excluded'
       AND r.status IN ('validated', 'approved', 'paid', 'closed')
       AND p.end_date >= NEW.effective_date
     LIMIT 1;
    IF v_locked IS NOT NULL THEN
        RAISE EXCEPTION 'Date d''effet dans une paie déjà validée (%) : passer par une régularisation', v_locked
            USING ERRCODE = 'check_violation';
    END IF;
    UPDATE nexora.payroll_run r SET stale = true
      FROM nexora.payroll_item i, nexora.payroll_period p
     WHERE i.run_id = r.id AND p.id = i.period_id AND i.employee_id = NEW.employee_id
       AND r.status IN ('calculated', 'reviewed') AND p.end_date >= NEW.effective_date;
    RETURN NEW;
END;
$$;
CREATE TRIGGER trg_salary_history_period_guard BEFORE INSERT ON nexora.salary_history
    FOR EACH ROW EXECUTE FUNCTION nexora.salary_history_period_guard();

-- =====================================================================
-- 14. Sécurité : isolation entreprise (§5, §39) + audit (§35)
-- Les données RH ne suivent pas l'isolation par succursale (V071) : la paie est calculée pour toute
-- l'entreprise ; la restriction Manager → son équipe est appliquée par le service (paramètre).
-- =====================================================================
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'department', 'employee', 'employee_contract', 'salary_history', 'pay_component', 'employee_pay_element',
        'payroll_period', 'payroll_run', 'payroll_item', 'payroll_earning', 'payroll_deduction',
        'attendance_record', 'leave_record', 'overtime_record', 'payroll_variable', 'payroll_rule',
        'employee_advance', 'advance_installment', 'advance_repayment', 'employee_advance_event',
        'employee_expense_claim', 'salary_payment', 'payslip', 'payroll_adjustment', 'employee_document'] LOOP
        PERFORM nexora.enable_company_rls(('nexora.' || t)::regclass);
    END LOOP;

    -- Lignes de calcul (item/gains/retenues) : tracées au niveau de l'exécution (calcul, validation,
    -- approbation) ; les documents ont leur propre audit sans contenu binaire.
    FOREACH t IN ARRAY ARRAY[
        'department', 'employee', 'employee_contract', 'salary_history', 'pay_component', 'employee_pay_element',
        'payroll_period', 'payroll_run', 'attendance_record', 'leave_record', 'overtime_record', 'payroll_variable',
        'payroll_rule', 'employee_advance', 'advance_installment', 'advance_repayment', 'employee_expense_claim',
        'salary_payment', 'payslip', 'payroll_adjustment'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_audit_%1$s ON nexora.%1$I', t);
        EXECUTE format(
            'CREATE TRIGGER trg_audit_%1$s AFTER INSERT OR UPDATE OR DELETE ON nexora.%1$I
               FOR EACH ROW EXECUTE FUNCTION nexora.audit_row()', t);
    END LOOP;
END;
$$;

SELECT nexora.attach_common_triggers();
