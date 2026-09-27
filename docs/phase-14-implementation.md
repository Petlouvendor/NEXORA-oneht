# NEXORA — Phase 14 — État de l'implémentation

Référence : [`phase-14-administration-parametrage-automatisation.md`](phase-14-administration-parametrage-automatisation.md).

## Principe directeur

Configuration **sans code** pour les besoins standards. Chaque paramètre a un **périmètre** ;
le niveau le plus fin gagne. Les changements sensibles sont **audités** et exigent un motif.
Les configurations sensibles sont **versionnées** — une nouvelle version ne réécrit jamais
l'historique. Les automatisations sont **explicables et désactivables** ; les opérations
financières critiques ne s'exécutent jamais sans règle et autorisation explicites (§46).

## Acquis réutilisé

`role` / `role_permission` (déjà company-scoped, §10-11), `approval_policy` /
`approval_request` / `approval_action` (Phase 11), `alert_rule` (Phase 7), `custom_field`
(Phase 8), `account_mapping` / `document_sequence` / `tax` (Phase 4-5), `notification`
(Phase 7), `audit_log` + `audit_row()` (V016/V030).

## Ce qui est livré — `V033`

| Objet | Rôle |
|---|---|
| `setting_definition` (référence, 12 clés seedées) | catalogue : type, périmètre maximal, sensibilité, `affects_future` |
| `setting` | valeurs posées par périmètre `system/group/company/establishment` ; résolution **establishment > company > group > system > défaut** ; RLS entreprise (`company_id` nullable pour les valeurs globales) ; audité |
| `workflow` / `workflow_run` | workflows d'approbation configurables + **moteur de règles testable** ; `is_tested` requis avant activation |
| `automation` / `automation_run` | planificateur (`schedule` jsonb) + exécuteurs + historique d'exécution avec `mode` live/test |
| `dunning_policy` (+ `customer.dunning_opt_out`) | relances clients à niveaux 1/2/3 (§19) |
| `config_version` | instantané daté d'un domaine (`numbering`, `accounting_defaults`, `taxes`, `approvals`, `pos`, `stock`) ; `effective_from`/`effective_to` ; statut draft/active/superseded |
| permissions | `admin.{settings,workflows,automations,config_versions,roles}` + rattachement aux rôles |
| RLS / audit | 149/161 tables protégées ; `setting`, `workflow`, `automation`, `dunning_policy`, `config_version` audités |

### Backend — `Nexora.{Application,Infrastructure}/Administration`

```
ISettingsService      DefinitionsAsync, EffectiveAsync, ResolveAsync (hiérarchie de périmètre),
                      SetAsync (valide type/enum + périmètre ≤ max_scope ; motif requis si sensible ;
                      renvoie des avertissements si affects_future)
ICustomRoleService    ListAsync, CreateAsync (dérivé d'un rôle de base + permissions), SetPermissionsAsync,
                      PreviewAsync (permissions effectives + nb d'affectations), DisableAsync (garde-fou :
                      rôle assigné → confirmation forcée, §45)
IWorkflowService      UpsertAsync, TestAsync (dry-run : conditions évaluées + effets prévus, aucun effet,
                      marque is_tested), ActivateAsync (exige un test ; crée/maj un approval_policy adossé),
                      EvaluateAsync (live : conditions → SubmitAsync Phase 11 → approval_request ; workflow_run)
IAutomationService    UpsertAsync (+ next_run_at calculé), RunNowAsync(preview), RunDueAsync, RunsAsync ;
                      exécuteurs réels : dunning (créances en retard → notifications, ignore les désinscrits),
                      low_stock_alert (reorder_point vs stock_balance), expiry_alert (lots proches péremption) ;
                      GetDunningPolicyAsync / SetDunningPolicyAsync
IConfigVersionService SnapshotAsync (capture le domaine + diff vs actif), ActivateAsync (date la fin de la
                      version précédente — historique préservé), ActiveAtAsync (quelle version à l'instant T)
IAdminTaskService     ListAsync : produits sans prix / sans taxe, comptes par défaut manquants,
                      utilisateurs sans rôle actif, sauvegarde non configurée, connecteurs en erreur,
                      workflows non testés, périodes échues non clôturées
```

**Moteur de règles** : conditions `{field, op, value}` avec `op ∈ eq/neq/gt/gte/lt/lte/in/contains`,
logique `all`/`any` ; comparaison numérique quand les deux membres sont des nombres, sinon
textuelle. Le mapping `trigger_event → request_type` d'approbation couvre dépense / remise /
remboursement / ajustement stock / changement de prix / réouverture période / achat / crédit / annulation.

### API — `v1` (`/admin`)

`GET settings/definitions` · `GET settings` · `PUT settings` — `admin.settings`
`GET/POST roles` · `PUT roles/{id}/permissions` · `GET roles/{id}/preview` · `POST roles/{id}/disable` — `admin.roles`
`GET/POST workflows` · `POST workflows/{id}/{test,activate,status}` · `POST workflows/evaluate` — `admin.workflows`
`GET/POST automations` · `POST automations/{id}/{run,status}` · `POST automations/run-due` · `GET automations/{id}/runs` · `GET/PUT dunning-policy` — `admin.automations`
`GET config-versions` · `POST config-versions/snapshot` · `POST config-versions/{id}/activate` · `GET config-versions/active-at` — `admin.config_versions`
`GET tasks` — `admin.settings`

### Frontend

`admin/AdminCenterPage.tsx` (route `/admin`, nav « Administration ») — onglets **Tâches**
(lacunes de configuration), **Paramètres** (par catégorie, édition au niveau entreprise avec
remontée des avertissements), **Rôles** (liste + permissions + désactivation), **Workflows**
(test avec montant + activation conditionnée au test), **Automatisations** (prévisualiser /
exécuter + prochain déclenchement), **Versions de config** (instantané + activation + fenêtre
d'effet). `adminApi.ts`. La nav « Administration » pointe désormais sur `/admin` ; l'ancien
`/settings` devient « Préférences ».

### Démo

`demo_abc_group.sql` : la Pharmacie limite `pos.max_discount_percent` à 10, impose
`stock.enforce_fefo` et `backup.enabled` ; `dunning_policy` « Relances standard » (3 niveaux) ;
automatisation `daily-dunning` (quotidienne 08h00). Nettoyage feuilles → racine étendu
(`automation(_run)`, `workflow(_run)`, `config_version`, `dunning_policy`, `setting`). Rejouable.

## Vérifications exécutées

**Tests d'intégration `Nexora.IntegrationTests/AdministrationTests` — 9/9** :

| Scénario (§47) | Vérifié |
|---|---|
| Résolution par périmètre | `pos.max_discount_percent` : entreprise = 10 ; surcharge établissement = 5 gagne avec l'établissement, 10 sans ; clé non surchargée → défaut de définition |
| Paramètre sensible | `stock.valuation_method` sans motif → refus ; avec motif → OK + avertissement « transactions futures » + entrée `audit_log` ; valeur enum hors liste → refus |
| Périmètre maximal | `finance.rounding_mode` (max company) posé à l'établissement → `settings.scope_too_fine` |
| Rôle personnalisé | création + `PreviewAsync` liste les permissions, 0 affectation, non système ; permission inconnue → refus |
| Garde-fou désactivation | rôle assigné → `DisableAsync(force:false)` refusé, `force:true` OK (§45) |
| Workflow de remise | activation avant test → refus ; test montant 5000 → `matched`, condition passée, approbation prévue ; test 1000 → non ; activation OK ; `EvaluateAsync` 4200 → `approvalRequestId` créé ; 500 → non |
| Automatisation de relance | 2 créances en retard (dont 1 client désinscrit) → `RunNowAsync` : `items_processed ≥ 1`, notification `dunning` pour le payeur, **aucune** pour le désinscrit |
| Versionnement de config | snapshot `numbering` v1 actif au −30 j ; modification d'une séquence ; snapshot v2 actif maintenant ; `ActiveAtAsync(−15 j)` = v1, `ActiveAtAsync(maintenant+1 min)` = v2 ; v1 `superseded` avec `effective_to` — historique préservé |
| Centre de tâches | produit créé sans prix → `products_without_price` présent, count ≥ 1, sévérité warning |

**Smoke API (démo)** : `/admin/tasks` → détecte `products_without_tax` ; `/admin/settings` →
12 valeurs effectives ; `PUT settings` sensible sans motif → **422**, avec motif → **200** ;
`roles`/`workflows`/`automations` → 200 ; run d'automatisation en prévisualisation → `success` ;
`config-versions/snapshot` → v1 brouillon.

**Total backend : 113 tests verts** (16 unitaires + 2 API + 95 intégration), build Debug **et**
Release 0 warning. DB : 33 migrations (V001–V033), **161 tables**, 149 avec RLS. Rebuild +
smoke + démo (rejouée 2×) OK. `tsc` strict + `vite build` OK.

## Écarts et décisions

- **Assistant de configuration initiale** (§5) : la checklist de démarrage (Phase 13) couvre la
  progression et le « reprendre plus tard » ; l'assistant pas-à-pas multi-écrans complet
  (création d'entreprise → activation) réutilisera les services livrés (settings, comptes par
  défaut, séquences, utilisateurs) — **défini**, non assemblé en un flux unique.
- **Exécuteurs d'automatisation** : `dunning`, `low_stock_alert`, `expiry_alert` sont **réels** ;
  `scheduled_report`, `supplier_reminder`, `reconcile_assist`, `archive`, `scheduled_export`
  enregistrent une exécution sans effet (« exécuteur défini »). Le **worker de fond** qui
  appelle `RunDueAsync` périodiquement relève de l'hébergement — l'API `run-due` est exposée.
- **Planificateur** (§18) : `frequency` once/hourly/daily/weekly/monthly + `atTime` calculent
  `next_run_at` ; la gestion fine des fuseaux et des `dayOfWeek`/`dayOfMonth` est simplifiée
  (base quotidienne) — le champ est stocké et affiché.
- **Workflows** : le moteur évalue les conditions (montant, catégorie, client, statut, horaire…)
  et, en cas de correspondance avec une clause d'approbation, adosse un `approval_policy` puis
  crée la demande via la Phase 11. Les **actions** (au-delà de l'approbation) et l'**escalade**
  sont enregistrées dans `workflow_run.decision` mais pas exécutées automatiquement.
- **Import/export de configuration** (§42-43) : le modèle (`config_version.snapshot`, jamais de
  secret) est en place ; l'endpoint d'import avec détection de conflits est **défini**.
- **Prévisualisation d'impact** (§41) : `TestAsync` (workflows) et `RunNowAsync(preview)`
  (automatisations) livrent la prévisualisation ; l'analyse « entreprises / utilisateurs /
  automatisations touchés » d'un changement de paramètre est **définie**.
- **Terminologie, documents, taxes détaillées, configuration POS/stock/achats/finance/compta**
  (§21-32) : s'appuient sur des tables existantes (`terminology_override`, `tax`,
  `document_sequence`, `account_mapping`, `sector_config`…) — pilotées via `setting` et les
  services de leurs phases respectives ; le centre d'administration les expose progressivement.

## Couverture des critères de sortie (§48)

| Critère | État |
|---|---|
| Centre administration | ✅ API `/admin` + écran multi-onglets |
| Assistant onboarding | ⚙️ checklist livrée ; flux pas-à-pas défini |
| Organisation / utilisateurs / rôles | ✅ rôles personnalisés + prévisualisation + garde-fous |
| Permissions | ✅ matrice ressource × action réutilisée ; rôles company-scoped |
| Champs personnalisés | ✅ Phase 8 (réutilisé) |
| Workflows / moteur de règles | ✅ conditions IF/THEN, testables avant activation |
| Automatisations / planificateur | ✅ relances/alertes réelles + historique + prévisualisation |
| Taxes / devise / numérotation / documents | ⚙️ tables existantes pilotées par `setting` + versionnées |
| POS / stock / achats / finance / compta configurables | ✅ via `setting` (12 clés seedées) + phases dédiées |
| Paramètres sectoriels / multi-entreprises / intégrations / offline | ✅ Phases 8-12 (réutilisées), pilotage centralisé |
| Sécurité | ✅ Phase 11 + `security.session_minutes` en `setting` sensible |
| Versionnement | ✅ `config_version` + « version active à l'instant T » |
| Sandbox / prévisualisation | ✅ dry-run workflows + preview automatisations |
| Scénarios testables | ✅ 9 scénarios §47 automatisés + smoke API |

## Suite — Phase 15

Qualité, Tests, Déploiement & Mise en Production : stratégie de tests, environnements,
validation fonctionnelle, tests financiers, charge, migration, lancement V1.0.
