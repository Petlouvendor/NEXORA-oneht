# Module « Paie, RH & avances sur salaire » — mise en œuvre

Spécification : [module-paie-rh-avances.md](module-paie-rh-avances.md) (transmise le 2026-09-24).
Migrations : `V075__payroll_hr.sql` (schéma), `V076__payroll_security_config.sql` (droits, paramètres, comptabilité,
modèles), `V077__payroll_group_mapping.sql` (rattachement au plan de groupe). Appliquées à `nexora` et `nexora_recette`.

## 1. Principes retenus

- **Le serveur est la seule autorité des montants** (§39) : brut, net, retenues, soldes et récupérations sont calculés par
  un moteur **pur et déterministe** (`Nexora.Application/Payroll/PayrollCalculator.cs`) ; le frontend n'envoie jamais un
  montant calculé, seulement des saisies (salaire, prime, heures…).
- **Explicabilité** (§11, §40) : chaque gain et chaque retenue est une ligne avec sa source (contrat, variable, heure sup.,
  absence, avance, règle légale et sa version), et chaque ligne de paie garde un calcul pas à pas (`trace`).
- **Verrous portés par la base** (§22, §31, §39) : une paie validée ne se modifie plus (triggers), un salaire ne s'écrase
  pas (historique immuable), une récupération ne dépasse jamais le solde, rien de finalisé ne se supprime, un paiement
  actif par ligne de paie, 4-yeux sur toutes les demandes.
- **Réutilisation du socle** : moteur comptable centralisé (`PostingRuleSet`), workflow d'approbation (Phase 11) pour les
  avances, notifications (Phase 34), impression PDF/Excel (`IPrintService`), audit immuable, RLS entreprise.

## 2. Correspondance avec le modèle proposé (§30)

| Table proposée | Mise en œuvre |
|---|---|
| employees | `employee` (+ `department`) — identifiants bancaires/fiscaux chiffrés AES-256-GCM, 4 derniers chiffres en clair |
| employee_contracts | `employee_contract` (jamais deux contrats actifs qui se chevauchent — contrainte EXCLUDE) |
| salary_history | `salary_history` (immuable ; ancien salaire posé par trigger ; date d'effet dans une paie validée refusée) |
| payroll_periods / payroll_runs | `payroll_period` (régulière ou hors cycle, sans chevauchement) / `payroll_run` (workflow) |
| payroll_items / earnings / deductions | `payroll_item` / `payroll_earning` / `payroll_deduction` (part patronale = `borne_by = employer`) |
| employee_advances / employee_loans | `employee_advance` (`kind` = avance, acompte, prêt) + `advance_installment` (échéancier versionné) |
| advance_repayments | `advance_repayment` (immuable ; trigger : solde, statut « soldée », imputation des échéances) |
| attendance / leave / overtime | `attendance_record`, `leave_record`, `overtime_record` |
| payslips / salary_payments | `payslip` (instantané figé + SHA-256) / `salary_payment` |
| payroll_adjustments | `payroll_adjustment` (reliée à la ligne d'origine) |
| employee_documents | `employee_document` (hors GED : accès réservé aux droits RH/paie) |
| audit_logs | `audit_log` existant (triggers) + `employee_advance_event` (étapes d'une avance) |
| — | `pay_component`, `employee_pay_element`, `payroll_variable`, `payroll_rule`, `employee_expense_claim` |

## 3. Cycle de paie (§3, §9, §22, §23)

`BROUILLON → CALCUL → CONTRÔLE (payroll.review) → VALIDATION (payroll.validate, verrouille) → APPROBATION FINALE
(payroll.approve, écritures) → PAIEMENT (payroll.pay, bulletins) → CLÔTURE (payroll.close)`.

- Séparation des tâches (paramètre `payroll.segregation_of_duties`, actif par défaut) : le calculateur ne valide ni
  n'approuve, le valideur n'approuve pas.
- Contrôles (§23) : contrat et salaire applicables, employé actif, absences / heures sup. / variables non validées signalées,
  récupérations plafonnées, net négatif, doublon d'employé, compte bancaire manquant pour un virement, écart de net
  inhabituel, heures sup. élevées, absence de règle légale validée. **Bloquant** = empêche le contrôle (corriger et
  recalculer, ou exclure l'employé avec motif) ; **avertissement** = doit être reconnu explicitement.
- Toute saisie de temps ou de salaire dans une paie calculée la marque « à recalculer » ; dans une paie validée, elle est
  refusée par la base (§18 « verrouiller après clôture »).
- Après approbation : régularisation uniquement (§36), intégrée à la paie suivante ou à une paie hors cycle.

## 4. Calcul (§10, §11, §15)

Brut = salaire de base proratisé (segments de salaire jour par jour, bases mensuelle / journalière / horaire, fréquences
mensuelle / bihebdomadaire / hebdomadaire) + heures sup. validées (heures × taux horaire × coefficient figé à la saisie)
+ éléments récurrents (fixe, % du salaire, % des ventes nettes, par jour travaillé) + variables approuvées
+ régularisations. Frais remboursés : hors brut, non imposables. Avantages en nature : dans le brut et les assiettes,
déduits du net.

Retenues : règles légales validées et en vigueur à la fin de période (pourcentage, fixe, barème progressif éventuellement
annualisé ; assiettes imposable, sociale, brute ou imposable après cotisations ; seuil, plafond, minimum, maximum), absences
(jours × salaire ÷ `payroll.days_per_month`), autres retenues plafonnées au net minimal, puis récupérations d'acomptes,
d'avances et de prêts plafonnées au net minimal et à `payroll.max_recovery_percent_of_net` — le reste demeure dû.

**Aucun barème n'est codé en dur** : les modèles Haïti fournis (ONA salarié et patronal 6 %, CFGDCT 1 %, IRI progressif
annuel) ont le statut « modèle », ne s'appliquent jamais d'office et doivent être adoptés, **vérifiés au regard des textes
en vigueur**, puis validés par une autre personne que leur auteur. Une règle validée est figée ; une évolution crée une
nouvelle version datée qui clôt la précédente.

## 5. Avances, acomptes, prêts (§12-§17)

Demande (employé ou RH) → contrôles de politique (% du salaire, avances multiples, nombre maximal, plafonds d'endettement
avances et prêts, alerte « proche du plafond ») → approbation par le workflow configuré (`approval_policy` des types
`salary_advance` / `employee_loan`, au moins un niveau, jamais par le demandeur ni par l'employé lui-même) → versement relié
à un compte de trésorerie (et à la caisse si une session est indiquée) → échéancier (prochaine paie, n échéances, % du net
ou remboursement direct) → récupérations par paie ou remboursement direct → « soldée » à zéro. Chaque étape est tracée
(`employee_advance_event`) et visible via `GET /salary-advances/{id}/history`. Échéancier modifiable avec
`payroll.advance_schedule` et un motif (version suivante, total = solde restant).

## 6. Finance (§32)

Nouvelles clés de correspondance et comptes créés pour chaque entreprise (et à l'initialisation d'une nouvelle entreprise) :
`salary_expense` 6410, `employer_charges_expense` 6450, `salaries_payable` 4210, `employee_advances` 4250,
`employee_loans` 4251, `payroll_withholdings` 4310, `payroll_other_deductions` 4280, `employee_reimbursements_payable` 4286 ;
journal **PA** « Journal de paie ». Dans un groupe doté d'un plan consolidé, rattachement automatique aux comptes de groupe
GA-PERS / GA-PERSREC / GA-PERSEXP (V077).

| Événement | Écriture |
|---|---|
| Versement d'avance / prêt | Dr 4250 / 4251 (tiers employé) — Cr trésorerie ; **jamais une charge** (§14) |
| Paie approuvée | Dr 6410 (brut − absences − avantages en nature) + Dr 6450 + Dr 4286 (frais) — Cr 4310 + Cr 4250 / 4251 (récupérations) + Cr 4280 + Cr 4210 (net, par employé) |
| Paiement de salaire | Dr 4210 (employé) — Cr trésorerie ; annulation = extourne + retour des fonds |
| Remboursement direct d'avance | Dr trésorerie — Cr 4250 / 4251 |
| Note de frais validée / payée | Dr charge — Cr 4286 (employé) / Dr 4286 — Cr trésorerie |

`GET /payroll/{id}/reconciliation` compare poste par poste le dossier RH et le grand livre, et par employé : net approuvé
− payé = solde du compte 4210 pour cette paie.

## 7. Sécurité et confidentialité (§5, §6, §35, §39)

24 permissions (`hr.*`, `payroll.*`). Rôles : **hr_manager** (nouveau), **employee** (nouveau, espace personnel),
manager (demandes de son équipe, sans salaires ; paramètre `payroll.manager_team_only`), comptable / senior / en chef
(calcul, contrôle, validation, paiement, clôture selon le niveau), direction (tout, dont approbation finale), auditeur
(lecture + audit), administrateurs techniques (**configuration seulement**, pas le contenu RH). Caissier, vendeur,
magasinier, acheteur : uniquement `payroll.self_service` (leurs propres bulletins s'ils ont un dossier).

- Tout est vérifié côté API et service : un identifiant hors périmètre (autre entreprise, autre employé) répond **404**,
  jamais la donnée ; RLS entreprise sur les 25 tables.
- L'espace employé (`/me/hr/*`) déduit l'employé du compte connecté : aucun identifiant d'employé n'est lu dans la requête.
- Lecture en clair des identifiants bancaires/fiscaux, téléchargement de documents RH et exports de rapports journalisés
  (`READ_SENSITIVE` dans `audit_log`, `export_log`).

## 8. API (§29)

| Spécification | Route |
|---|---|
| GET/POST /employees, GET /employees/{id} | idem (+ PUT, /status, /user, /sensitive, /history) |
| POST /employees/{id}/contracts | idem (+ /employee-contracts/{id}/end, /salary-history, /salary-changes, /compensation, /pay-elements) |
| GET/POST /payroll/periods, POST /payroll/periods/{id}/calculate | idem |
| POST /payroll/{id}/approve, /pay | idem (+ /review, /validate, /return, /cancel, /close, /payments, /payslips, /reconciliation) |
| POST /employees/{id}/salary-advances | idem (+ /preview) |
| POST /salary-advances/{id}/approve, /pay, /repay ; GET /history | idem (+ /reject, /cancel, PUT /schedule) |
| GET /employees/{id}/payslips | idem (+ /payslips/{id}/file, /verify) |
| — | /hr/attendance, /hr/leaves, /hr/overtime, /hr/variables, /hr/approvals, /hr/expense-claims, /hr/dashboard, /hr/insights, /hr/reports/{key}(/export), /hr/audit, /payroll/rules, /payroll/components, /payroll/adjustments, /departments, /me/hr/* |

Paiements (salaires, avances, remboursements, frais) : clé d'idempotence obligatoire + identifiant d'opération ; un paiement
groupé dérive un identifiant par ligne : le même envoi rejoue, ne paie jamais deux fois.

## 9. Rapports, tableau de bord, intelligence, notifications (§27, §28, §33, §34)

11 rapports exportables en vrai Excel, PDF ou CSV (masse salariale, net par période, brut par département, retenues par
catégorie, avances en cours / soldées / par employé, dette du personnel, paies impayées, écarts entre périodes, coût
employeur). Tableau de bord des 8 indicateurs §28. Constats déterministes : variation de masse salariale, hausse des heures
sup., avances fréquentes, encours proche du plafond, compte bancaire partagé, paiement possiblement en double, comparaison
des coûts. Assistant : « Combien reste-t-il d'avances à récupérer ? » et « Quelle est la masse salariale de ce mois ? »
(réservé aux droits paie, chiffres lus en base). Tâche planifiée `payroll.scan_alerts` (échéances d'avance proches,
anomalies). Notifications : demandes à valider, décisions, versements, paie à contrôler / valider / approuver / payer,
anomalies, salaire versé et bulletin disponible.

## 10. Frontend

`src/hr/` : **RH & Paie** (`/hr`, onglets par permission), dossier employé (`/hr/employees/:id`), paie (`/hr/payroll/:runId` :
étapes, anomalies, détail explicable, paiements, bulletins, rapprochement), avance (`/hr/advances/:id`),
**Mon espace RH** (`/me/hr`). Groupe de navigation « Ressources humaines ». Aucune saisie par `window.prompt`.

## 11. Vérification

| §37 | Preuve |
|---|---|
| 11-12 | `PayrollTests.Employee_contract_and_salary_history_are_kept_immutable_and_reconstructible` |
| 13-17 | `…March_payroll_combines_base_bonus_absences_and_validated_overtime…` (brut 89 326,92 / net 83 326,92 recalculés à la main) |
| 18-20, 26, 27, 29, 30 | `…Spec_example_advance_of_20000_is_recovered_over_two_payrolls…` (exemple §14 : 60 000, avance 20 000 → 10 000 + 10 000 → solde 0) |
| 21-22 | `…Several_advances_stay_separate_recovery_never_exceeds_balance…` (service **et** base) |
| 23-24 | `…Employee_sees_only_own_payslips_and_cashier_sees_no_salary` + `PayrollEndpointsTests` (33 routes refusées au caissier) |
| 25 | `…Employees_of_another_company_are_invisible…` (service + RLS avec le rôle applicatif réel) |
| 28 | `…A_closed_payroll_is_corrected_only_through_a_linked_approved_regularization` |
| règles | `…Legal_rules_are_validated_by_another_person_frozen_and_versioned` |
| SoD / anomalies | `…Segregation_of_duties_blocking_anomalies_and_manager_team_scope_are_enforced` |

Tests : 17 unitaires (`PayrollCalculatorTests`), 9 intégration (`PayrollTests`), 37 API (`PayrollEndpointsTests`),
5 frontend (`hrApi.test.ts`).

## 12. Limites connues

- Les **taux et barèmes haïtiens fournis sont des modèles à vérifier** : ils n'ont pas été contrôlés contre la loi de
  finances en vigueur et ne s'appliquent qu'après validation par l'entreprise.
- Le paiement groupé par virement enregistre les paiements et leur écriture ; il ne génère pas de fichier d'ordre de virement
  bancaire, et le mobile money n'est pas transmis à un opérateur (compte de trésorerie « mobile money » seulement).
- Le prêt au personnel est sans intérêt calculé (conditions en texte libre).
- Le solde de tout compte d'un salarié sortant se fait par une paie hors cycle ; il n'est pas calculé automatiquement
  (indemnités de fin de contrat non modélisées).
- Les données RH ne suivent pas l'isolation par succursale (V071) : la paie est tenue au niveau de l'entreprise, la
  restriction d'un Manager à son équipe est appliquée par le service.
