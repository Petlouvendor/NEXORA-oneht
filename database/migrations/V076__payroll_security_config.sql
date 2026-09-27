-- =============================================================================
-- NEXORA — V076 — Module « Paie, RH & avances » — sécurité, paramétrage, comptabilité
-- (docs/module-paie-rh-avances.md §5, §6, §11, §15-§16, §32-§34 ; schéma : V075)
--
--   1. permissions fines (une action sensible = une permission) et rôles « Responsable RH », « Employé » ;
--      le caissier ne reçoit AUCUN droit de lecture des salaires (§5) — seulement son espace personnel ;
--      l'administrateur système ne reçoit que la configuration, jamais le contenu RH (§6) ;
--   2. paramètres de paie configurables (base journalière, heures sup, plafonds d'avance, séparation des tâches…) ;
--   3. comptes de paie, correspondances comptables et journal de paie « PA » pour chaque entreprise, et
--      dans l'initialisation d'une nouvelle entreprise (V072) ;
--   4. composants de paie système et MODÈLES de règles légales (jamais appliqués tant qu'une entreprise ne
--      les a pas adoptés puis validés — §11, note finale du cahier) ;
--   5. types d'approbation (avance, prêt), notifications, tâche planifiée d'alertes.
-- =============================================================================
SET search_path TO nexora, public;

-- =====================================================================
-- 1. Permissions (§5, §6)
-- =====================================================================
INSERT INTO nexora.permission (code, name, module, action) VALUES
    ('hr.view_employees',           'RH — consulter les dossiers employés (sans salaire)',            'hr',      'view_employees'),
    ('hr.manage_employees',         'RH — créer / modifier dossiers, contrats et documents',          'hr',      'manage_employees'),
    ('hr.manage_time',              'RH — saisir présences, congés, heures sup. et variables',        'hr',      'manage_time'),
    ('hr.approve_requests',         'RH — valider congés, présences, heures sup. et variables',       'hr',      'approve_requests'),
    ('payroll.view_salaries',       'Paie — consulter salaires, paies et bulletins de tous',          'payroll', 'view_salaries'),
    ('payroll.manage_compensation', 'Paie — modifier la rémunération (salaire, éléments récurrents)', 'payroll', 'manage_compensation'),
    ('payroll.view_sensitive',      'Paie — voir en clair les identifiants bancaires et fiscaux',     'payroll', 'view_sensitive'),
    ('payroll.calculate',           'Paie — créer les périodes et calculer la paie',                  'payroll', 'calculate'),
    ('payroll.review',              'Paie — contrôle RH / comptable',                                 'payroll', 'review'),
    ('payroll.validate',            'Paie — valider (verrouille la paie)',                             'payroll', 'validate'),
    ('payroll.approve',             'Paie — approbation finale (écritures de paie)',                   'payroll', 'approve'),
    ('payroll.pay',                 'Paie — enregistrer les paiements de salaires',                   'payroll', 'pay'),
    ('payroll.close',               'Paie — clôturer une période',                                    'payroll', 'close'),
    ('payroll.adjust',              'Paie — demander une régularisation',                             'payroll', 'adjust'),
    ('payroll.approve_adjustment',  'Paie — approuver une régularisation',                            'payroll', 'approve_adjustment'),
    ('payroll.advance_request',     'Avances — créer une demande pour un employé',                    'payroll', 'advance_request'),
    ('payroll.advance_approve',     'Avances — approuver / refuser une avance ou un prêt',            'payroll', 'advance_approve'),
    ('payroll.advance_pay',         'Avances — verser une avance, enregistrer un remboursement',      'payroll', 'advance_pay'),
    ('payroll.advance_schedule',    'Avances — modifier un échéancier de récupération',               'payroll', 'advance_schedule'),
    ('payroll.expense_approve',     'Frais — valider / payer un remboursement de frais',              'payroll', 'expense_approve'),
    ('payroll.configure',           'Paie — paramètres, composants et règles légales',                'payroll', 'configure'),
    ('payroll.reports',             'Paie — tableau de bord et rapports globaux',                     'payroll', 'reports'),
    ('payroll.audit',               'Paie — consulter le journal d''audit RH / paie',                 'payroll', 'audit'),
    ('payroll.self_service',        'Espace employé — ses propres données, bulletins et demandes',    'payroll', 'self_service')
ON CONFLICT (code) DO NOTHING;

INSERT INTO nexora.role (name, description, system_role) VALUES
    ('hr_manager', 'Responsable RH — dossiers, contrats, temps, avances, préparation de la paie', true),
    ('employee',   'Employé — espace personnel (bulletins, avances, congés, frais)', true)
ON CONFLICT DO NOTHING;

INSERT INTO nexora.role_permission (role_id, permission_id)
SELECT r.id, p.id
FROM (
    SELECT 'owner_direction' AS role_name, code AS perm FROM nexora.permission WHERE module IN ('hr', 'payroll')
    UNION ALL
    SELECT * FROM (VALUES
    ('hr_manager', 'hr.view_employees'), ('hr_manager', 'hr.manage_employees'), ('hr_manager', 'hr.manage_time'),
    ('hr_manager', 'hr.approve_requests'), ('hr_manager', 'payroll.view_salaries'),
    ('hr_manager', 'payroll.manage_compensation'), ('hr_manager', 'payroll.view_sensitive'),
    ('hr_manager', 'payroll.calculate'), ('hr_manager', 'payroll.review'), ('hr_manager', 'payroll.adjust'),
    ('hr_manager', 'payroll.advance_request'), ('hr_manager', 'payroll.advance_approve'),
    ('hr_manager', 'payroll.advance_schedule'), ('hr_manager', 'payroll.expense_approve'),
    ('hr_manager', 'payroll.reports'), ('hr_manager', 'payroll.self_service'),
    -- Manager : valide les demandes de son équipe, sans voir les salaires (§6).
    ('manager', 'hr.view_employees'), ('manager', 'hr.approve_requests'), ('manager', 'payroll.advance_approve'),
    ('manager', 'payroll.self_service'),
    -- Comptables : calcul / contrôle / paiement / intégration finance selon le niveau (§6).
    ('accountant', 'hr.view_employees'), ('accountant', 'payroll.view_salaries'), ('accountant', 'payroll.calculate'),
    ('accountant', 'payroll.review'), ('accountant', 'payroll.pay'), ('accountant', 'payroll.advance_pay'),
    ('accountant', 'payroll.reports'), ('accountant', 'payroll.self_service'),
    ('senior_accountant', 'hr.view_employees'), ('senior_accountant', 'payroll.view_salaries'),
    ('senior_accountant', 'payroll.calculate'), ('senior_accountant', 'payroll.review'),
    ('senior_accountant', 'payroll.validate'), ('senior_accountant', 'payroll.pay'),
    ('senior_accountant', 'payroll.adjust'), ('senior_accountant', 'payroll.advance_pay'),
    ('senior_accountant', 'payroll.expense_approve'), ('senior_accountant', 'payroll.reports'),
    ('senior_accountant', 'payroll.self_service'),
    ('chief_accountant', 'hr.view_employees'), ('chief_accountant', 'payroll.view_salaries'),
    ('chief_accountant', 'payroll.calculate'), ('chief_accountant', 'payroll.review'),
    ('chief_accountant', 'payroll.validate'), ('chief_accountant', 'payroll.pay'), ('chief_accountant', 'payroll.close'),
    ('chief_accountant', 'payroll.adjust'), ('chief_accountant', 'payroll.approve_adjustment'),
    ('chief_accountant', 'payroll.advance_pay'), ('chief_accountant', 'payroll.expense_approve'),
    ('chief_accountant', 'payroll.reports'), ('chief_accountant', 'payroll.audit'),
    ('chief_accountant', 'payroll.self_service'),
    -- Auditeur : lecture et audit, sans modification (§6).
    ('auditor', 'hr.view_employees'), ('auditor', 'payroll.view_salaries'), ('auditor', 'payroll.reports'),
    ('auditor', 'payroll.audit'),
    -- Administrateurs techniques : configuration seulement, pas d'accès automatique au contenu RH (§6).
    ('super_administrator', 'payroll.configure'), ('super_administrator', 'payroll.self_service'),
    ('group_administrator', 'payroll.configure'), ('group_administrator', 'payroll.self_service'),
    -- Employé et rôles opérationnels : leur propre espace uniquement (§5).
    ('employee', 'payroll.self_service'), ('cashier', 'payroll.self_service'),
    ('senior_cashier', 'payroll.self_service'), ('salesperson', 'payroll.self_service'),
    ('stock_manager', 'payroll.self_service'), ('purchasing_manager', 'payroll.self_service')
    ) AS v(role_name, perm)
) g
JOIN nexora.role r ON r.name = g.role_name AND r.company_id IS NULL
JOIN nexora.permission p ON p.code = g.perm
ON CONFLICT DO NOTHING;

-- =====================================================================
-- 2. Paramètres de paie (§11, §15, §16, §18, §19, §22, §23, §25)
-- =====================================================================
INSERT INTO nexora.setting_definition (key, category, value_type, default_value, allowed_values, max_scope, is_sensitive, affects_future, description) VALUES
    ('payroll.day_count_basis', 'payroll', 'enum', '"calendar"', '["calendar","working"]', 'company', false, true,
     'Décompte des jours (prorata, absences) : jours calendaires ou jours ouvrés'),
    ('payroll.days_per_month', 'payroll', 'number', '30', '[]', 'company', false, true,
     'Base journalière : salaire mensuel ÷ ce nombre de jours (30 calendaires, 26 ouvrés…)'),
    ('payroll.working_week_days', 'payroll', 'json', '[1,2,3,4,5,6]', '[]', 'company', false, true,
     'Jours ouvrés de la semaine (1 = lundi … 7 = dimanche)'),
    ('payroll.default_weekly_hours', 'payroll', 'number', '48', '[]', 'company', false, true,
     'Durée hebdomadaire de référence (taux horaire dérivé d''un salaire mensuel)'),
    ('payroll.overtime_coefficients', 'payroll', 'json', '{"standard":1.5,"night":1.5,"weekend":2,"holiday":2}', '[]',
     'company', false, true, 'Coefficients des heures supplémentaires par type (figés sur chaque saisie)'),
    ('payroll.overtime_alert_hours', 'payroll', 'number', '40', '[]', 'company', false, false,
     'Heures supplémentaires d''une période au-delà desquelles une anomalie est signalée'),
    ('payroll.advance_max_percent_of_salary', 'payroll', 'number', '50', '[]', 'company', false, true,
     'Montant maximal d''une avance ou d''un acompte, en % du salaire mensuel'),
    ('payroll.allow_multiple_advances', 'payroll', 'boolean', 'true', '[]', 'company', false, true,
     'Autoriser plusieurs avances en cours simultanément (§16)'),
    ('payroll.max_open_advances', 'payroll', 'number', '3', '[]', 'company', false, true,
     'Nombre maximal d''avances en cours par employé'),
    ('payroll.advance_ceiling_percent', 'payroll', 'number', '100', '[]', 'company', false, true,
     'Plafond d''endettement « avances et acomptes » en % du salaire mensuel (§16)'),
    ('payroll.loan_ceiling_percent', 'payroll', 'number', '300', '[]', 'company', false, true,
     'Plafond d''endettement « prêts » en % du salaire mensuel (§16, §17)'),
    ('payroll.debt_near_limit_percent', 'payroll', 'number', '80', '[]', 'company', false, false,
     'Alerte « proche du plafond » : part du plafond d''endettement déjà consommée (%)'),
    ('payroll.frequent_advance_count', 'payroll', 'number', '3', '[]', 'company', false, false,
     'Avances demandées sur 90 jours au-delà desquelles l''employé est signalé (§33)'),
    ('payroll.max_recovery_percent_of_net', 'payroll', 'number', '100', '[]', 'company', false, true,
     'Récupérations (avances + prêts) d''une paie, en % du net avant récupération'),
    ('payroll.min_net_amount', 'payroll', 'number', '0', '[]', 'company', false, true,
     'Net minimal préservé : une récupération ne le fait jamais descendre en dessous (§15)'),
    ('payroll.variance_alert_percent', 'payroll', 'number', '20', '[]', 'company', false, false,
     'Écart de net d''une période à l''autre au-delà duquel une anomalie est signalée (§23)'),
    ('payroll.segregation_of_duties', 'payroll', 'boolean', 'true', '[]', 'company', false, true,
     'Séparation des tâches : le calculateur ne valide ni n''approuve, le valideur n''approuve pas (§39)'),
    ('payroll.manager_team_only', 'payroll', 'boolean', 'true', '[]', 'company', false, true,
     'Un Manager sans droit RH ne voit et ne valide que les demandes de son équipe'),
    ('payroll.annual_leave_days', 'payroll', 'number', '15', '[]', 'company', false, false,
     'Droit annuel à congé payé (jours) — signalé si dépassé'),
    ('payroll.advance_due_notice_days', 'payroll', 'number', '5', '[]', 'company', false, false,
     'Préavis (jours) de la notification « échéance d''avance proche » (§34)'),
    ('payroll.payslip_legal_mentions', 'payroll', 'string',
     '"Bulletin établi selon les règles de paie validées par l''entreprise. À conserver sans limitation de durée."',
     '[]', 'company', false, false, 'Mentions légales imprimées sur le bulletin de paie (§25)')
ON CONFLICT (key) DO NOTHING;

-- =====================================================================
-- 3. Comptabilité de la paie (§32) : comptes, correspondances, journal « PA »
-- =====================================================================
ALTER TABLE nexora.account_mapping DROP CONSTRAINT account_mapping_key_chk;
ALTER TABLE nexora.account_mapping ADD CONSTRAINT account_mapping_key_chk CHECK (mapping_key IN (
    'sales_revenue','sales_tax_output','accounts_receivable','accounts_payable',
    'purchases','inventory','cogs','purchase_tax_input','expense_default',
    'cash_short_over','fx_gain','fx_loss','rounding','bank_charges','suspense',
    'cash','bank','mobile_money','retained_earnings','current_year_earnings',
    'inventory_adjustment','inventory_loss','goods_in_transit','purchase_price_variance',
    'sales_returns','store_credit','tips_payable','bad_debt',
    -- Paie (V076)
    'salary_expense','employer_charges_expense','salaries_payable','employee_advances','employee_loans',
    'payroll_withholdings','payroll_other_deductions','employee_reimbursements_payable'));

-- Idempotente : un compte, une correspondance ou un journal déjà présent n'est ni dupliqué ni modifié.
CREATE OR REPLACE FUNCTION nexora.bootstrap_payroll_defaults(p_company uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_chart uuid;
BEGIN
    SELECT id INTO v_chart FROM nexora.chart_of_accounts WHERE company_id = p_company AND status = 'active'
     ORDER BY created_at LIMIT 1;
    IF v_chart IS NULL THEN
        RETURN;
    END IF;

    INSERT INTO nexora.account (chart_of_accounts_id, company_id, code, name, account_type)
    SELECT v_chart, p_company, a.code, a.name, a.account_type
    FROM (VALUES
        ('4210', 'Personnel — rémunérations dues',               'liability'),
        ('4250', 'Personnel — avances et acomptes',              'asset'),
        ('4251', 'Personnel — prêts',                            'asset'),
        ('4280', 'Personnel — autres retenues à reverser',       'liability'),
        ('4286', 'Personnel — frais à rembourser',               'liability'),
        ('4310', 'Organismes sociaux et État — retenues sur salaires', 'liability'),
        ('6410', 'Rémunérations du personnel',                   'expense'),
        ('6450', 'Charges sociales patronales',                  'expense')
    ) AS a(code, name, account_type)
    ON CONFLICT (chart_of_accounts_id, code) DO NOTHING;

    INSERT INTO nexora.account_mapping (company_id, mapping_key, account_id)
    SELECT p_company, m.mapping_key, a.id
    FROM (VALUES
        ('salaries_payable', '4210'), ('employee_advances', '4250'), ('employee_loans', '4251'),
        ('payroll_other_deductions', '4280'), ('employee_reimbursements_payable', '4286'),
        ('payroll_withholdings', '4310'), ('salary_expense', '6410'), ('employer_charges_expense', '6450')
    ) AS m(mapping_key, code)
    JOIN nexora.account a ON a.chart_of_accounts_id = v_chart AND a.code = m.code
    ON CONFLICT (company_id, mapping_key) DO NOTHING;

    INSERT INTO nexora.journal (company_id, code, name, journal_type)
    VALUES (p_company, 'PA', 'Journal de paie', 'payroll')
    ON CONFLICT (company_id, code) DO NOTHING;
END;
$$;
GRANT EXECUTE ON FUNCTION nexora.bootstrap_payroll_defaults(uuid) TO nexora_app;

SELECT nexora.bootstrap_payroll_defaults(c.id) FROM nexora.company c;

-- Toute nouvelle entreprise (assistant d'installation, création d'entreprise) reçoit aussi la paie.
ALTER FUNCTION nexora.bootstrap_company_defaults(uuid, uuid, int) RENAME TO bootstrap_company_defaults_v072;
CREATE OR REPLACE FUNCTION nexora.bootstrap_company_defaults(p_company uuid, p_currency uuid, p_year int)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    PERFORM nexora.bootstrap_company_defaults_v072(p_company, p_currency, p_year);
    PERFORM nexora.bootstrap_payroll_defaults(p_company);
END;
$$;
GRANT EXECUTE ON FUNCTION nexora.bootstrap_company_defaults(uuid, uuid, int) TO nexora_app;
GRANT EXECUTE ON FUNCTION nexora.bootstrap_company_defaults_v072(uuid, uuid, int) TO nexora_app;

-- =====================================================================
-- 4. Composants de paie système (§10, §20, §21)
-- Pour une retenue, « taxable » signifie : vient en déduction des assiettes fiscale et sociale.
-- =====================================================================
INSERT INTO nexora.pay_component (company_id, code, name, kind, category, taxable, subject_to_social, in_gross, cash, requires_legal_basis) VALUES
    (NULL, 'BASE',            'Salaire de base',                         'earning',   'base',            true,  true,  true,  true,  false),
    (NULL, 'OVERTIME',        'Heures supplémentaires',                  'earning',   'overtime',        true,  true,  true,  true,  false),
    (NULL, 'BONUS',           'Prime ponctuelle',                        'earning',   'bonus',           true,  true,  true,  true,  false),
    (NULL, 'BONUS_PERF',      'Prime de performance',                    'earning',   'bonus',           true,  true,  true,  true,  false),
    (NULL, 'BONUS_RECURRING', 'Prime récurrente',                        'earning',   'bonus',           true,  true,  true,  true,  false),
    (NULL, 'COMMISSION',      'Commission sur ventes',                   'earning',   'commission',      true,  true,  true,  true,  false),
    (NULL, 'TRANSPORT',       'Indemnité de transport',                  'earning',   'allowance',       true,  true,  true,  true,  false),
    (NULL, 'HOUSING',         'Indemnité de logement',                   'earning',   'allowance',       true,  true,  true,  true,  false),
    (NULL, 'BENEFIT_KIND',    'Avantage en nature',                      'earning',   'benefit_in_kind', true,  true,  true,  false, false),
    (NULL, 'REIMBURSEMENT',   'Remboursement de frais',                  'earning',   'reimbursement',   false, false, false, true,  false),
    (NULL, 'ADJ_EARNING',     'Rappel de salaire (régularisation)',      'earning',   'adjustment',      true,  true,  true,  true,  false),
    (NULL, 'ABSENCE',         'Absence non rémunérée',                   'deduction', 'absence',         true,  true,  false, true,  false),
    (NULL, 'ADVANCE',         'Récupération d''avance sur salaire',      'deduction', 'advance',         false, false, false, true,  false),
    (NULL, 'LOAN',            'Échéance de prêt au personnel',           'deduction', 'loan',            false, false, false, true,  false),
    (NULL, 'OTHER_DEDUCTION', 'Autre retenue documentée',                'deduction', 'other_deduction', false, false, false, true,  false),
    (NULL, 'DISCIPLINARY',    'Retenue disciplinaire (base légale requise)', 'deduction', 'other_deduction', false, false, false, true, true),
    (NULL, 'ADJ_DEDUCTION',   'Trop-perçu (régularisation)',             'deduction', 'adjustment',      true,  true,  false, true,  false)
ON CONFLICT ON CONSTRAINT pay_component_uk DO NOTHING;

-- =====================================================================
-- 5. Modèles de règles légales — Haïti (§11 : « adaptées à la juridiction et validées par le responsable
-- compétent »). Statut « template » : JAMAIS appliqués. Une entreprise les adopte (copie en brouillon),
-- vérifie taux, assiettes et barèmes au regard des textes en vigueur, puis les valide.
-- =====================================================================
INSERT INTO nexora.payroll_rule (company_id, code, name, jurisdiction, rule_type, base_type, method, rate, brackets, annualize,
                                 valid_from, version, status, legal_reference, notes) VALUES
    (NULL, 'HT_ONA_EMPLOYEE', 'ONA — cotisation salariale (modèle à vérifier)', 'HT', 'employee_contribution', 'social',
     'percentage', 6, '[]', false, DATE '2020-01-01', 1, 'template', 'Office National d''Assurance-Vieillesse',
     'Modèle indicatif : taux et assiette à confirmer par le responsable compétent avant validation.'),
    (NULL, 'HT_ONA_EMPLOYER', 'ONA — cotisation patronale (modèle à vérifier)', 'HT', 'employer_contribution', 'social',
     'percentage', 6, '[]', false, DATE '2020-01-01', 1, 'template', 'Office National d''Assurance-Vieillesse',
     'Modèle indicatif : taux et assiette à confirmer par le responsable compétent avant validation.'),
    (NULL, 'HT_CFGDCT', 'CFGDCT — contribution sur salaire (modèle à vérifier)', 'HT', 'income_tax', 'taxable',
     'percentage', 1, '[]', false, DATE '2020-01-01', 1, 'template',
     'Fonds de Gestion et de Développement des Collectivités Territoriales',
     'Modèle indicatif : taux, seuil et assiette à confirmer avant validation.'),
    (NULL, 'HT_IRI', 'Impôt sur le revenu — barème progressif annuel (modèle à vérifier)', 'HT', 'income_tax',
     'taxable_after_social', 'progressive', NULL,
     '[{"from":0,"to":60000,"rate":0},{"from":60000,"to":240000,"rate":10},{"from":240000,"to":480000,"rate":15},{"from":480000,"to":1000000,"rate":25},{"from":1000000,"to":null,"rate":30}]',
     true, DATE '2020-01-01', 1, 'template', 'Impôt sur le revenu des personnes physiques (barème annuel)',
     'Modèle indicatif : tranches exprimées en HTG par an, à confirmer au regard de la loi de finances en vigueur.')
ON CONFLICT ON CONSTRAINT payroll_rule_version_uk DO NOTHING;

-- =====================================================================
-- 6. Workflow d'approbation (Phase 11) : avances et prêts au personnel
-- =====================================================================
ALTER TABLE nexora.approval_request DROP CONSTRAINT approval_request_type_chk;
ALTER TABLE nexora.approval_request ADD CONSTRAINT approval_request_type_chk CHECK (request_type IN (
    'expense','discount','refund','purchase','credit_sale','stock_adjustment',
    'price_change','void','period_close','other','salary_advance','employee_loan'));
ALTER TABLE nexora.approval_policy DROP CONSTRAINT approval_policy_request_type_chk;
ALTER TABLE nexora.approval_policy ADD CONSTRAINT approval_policy_request_type_chk CHECK (request_type IN (
    'expense','discount','refund','purchase','credit_sale','stock_adjustment',
    'price_change','void','period_close','other','salary_advance','employee_loan'));

-- =====================================================================
-- 7. Notifications (§34) et tâche planifiée
-- =====================================================================
INSERT INTO nexora.message_template (company_id, key, channel, locale, subject, body, description, variables) VALUES
(NULL, 'payroll.event', 'in_app', 'fr',
 '{{event.title}}',
 '{{event.detail}}',
 'Module paie §34 — avance à valider / décidée / versée / échéance proche, paie à valider / approuvée, paiement, anomalie, bulletin disponible',
 '["event.title","event.detail"]'::jsonb)
ON CONFLICT ON CONSTRAINT message_template_key_uk DO NOTHING;

INSERT INTO nexora.job_definition (key, name, category, description, owner) VALUES
    ('payroll.scan_alerts', 'Alertes paie & avances', 'business',
     'Échéances d''avance proches, avances fréquentes ou proches du plafond, écarts de masse salariale et d''heures '
     'supplémentaires, anomalies de paiement (module paie §33, §34).', 'scheduler')
ON CONFLICT (key) DO NOTHING;
