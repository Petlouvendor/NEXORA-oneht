# Phase 43 — Intelligence métier & recommandations — implémentation

**Statut : livré et vérifié.** Migration `V056` (5 tables neuves, 4 permissions, 1 job planifié).
6 tests d'intégration (`IntelligenceEngineTests`), 3 tests API (`IntelligenceEndpointsTests`),
5 tests frontend (`intelligenceApi.test.ts`).
**322 tests backend** verts (18 unit + 32 API + 272 intégration), Debug **et** Release, 0 warning,
vérifiés après un rebuild complet de la base (`V001`→`V056`, 230 tables). 81 tests frontend.
`tsc` + `vite build` verts.

Le socle décisionnel existait déjà et reste **validé** : Command Center + dashboards par rôle,
Ask Nexora déterministe (Phase 7) ; moteur de fraude à signaux + revue humaine (Phase 11) ;
correspondance à trois voies achats (Phase 26) ; portefeuille créances (Phase 27) ; trésorerie
(Phase 28) ; insights, prévisions, budgets, catalogue KPI gouverné (Phase 42). L'amélioration
ciblée de cette phase ajoute la couche manquante entre **« on a détecté »** (Phase 42 `insight`)
et **« voici quoi faire »** : des **recommandations** expliquées, scorées et dotées d'une boucle
d'apprentissage (§2, §4-5, §31, §33, §46), un **scoring explicable** (§35), des **règles
configurables** (§34), une **tâche générique** (comblant un vrai manque — aucune table de tâche
non liée à un groupe n'existait), et des **assistants par rôle** qui réutilisent tels quels les
tableaux de bord déjà livrés en Phase 7 plutôt que de les redériver (§6-10, §28-30).

### Socle réutilisé

- **Phase 7** — `IKpiService.{CommandCenterAsync,ManagerDashboardAsync,CashierDashboardAsync}`
  (briefings par rôle §6-10, sans redérivation), `IAskNexora` (assistant conversationnel §28-30,
  déterministe, sans LLM), `IAlertCenter`, `IStockReports`/`IReorderService` (gabarit repris pour
  la règle stock)
- **Phase 26** — `purchase_order`/`goods_receipt`/`supplier_invoice.match_status` → score fournisseur
- **Phase 27** — `ICreditPortfolioQueries.CollectionsAsync` → priorisation du recouvrement (§18) et
  score de risque créance (§35), sans dupliquer l'agrégation par tranche d'ancienneté
- **Phase 28** — `ITreasuryQueries.LiquidityAsync` → risque de trésorerie (§20), pas un quatrième
  calcul de trésorerie
- **Phase 29/42** — `IBudgetReports.BudgetVsActualAsync` → projection de dépassement budgétaire
  (§21), réutilisée telle quelle plutôt que recalculée
- **Phase 34** — `INotificationService.NotifyAsync` (`source: "automation"`, seule valeur du
  `CHECK` cohérente avec un déclenchement planifié)
- **Phase 41** — `SchedulerRunner`/`IJobRegistry` → 1 nouveau job nommé
- **Phase 42** — `insight`/`forecast` (Phase 42) réexposés sous `/intelligence/{insights,anomalies,
  forecasts}` sans duplication de données ni de logique

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2-5 | Principe données→recommandation→validation→action→mesure ; niveaux de confiance expliqués ; explication (pourquoi/données/impact/action/résultat attendu) | `recommendation` — `why`, `evidence` (jsonb), `impact_estimate` (jsonb, toujours qualifié d'estimation), `proposed_action`, `expected_result`, `confidence_level` **+ `confidence_reason`** en clair (§4 « explication du niveau ») |
| 3 | Types d'intelligence (règles/statistique/tendances/prévisions/scoring/recommandations/classification/anomalies/assistants) | Règles → `IRecommendationService` (5 générateurs) ; statistique/tendances/prévisions → Phase 42 `IForecastService` (réexposé) ; scoring → `IScoringService` (3 scoreurs) ; anomalies → Phase 42 `IInsightService` (réexposé) ; assistants → `IAssistantService`. Classification arbitraire — **backlog** (aucun cas d'usage concret identifié dans cette phase) |
| 6-10 | Assistants direction/manager/comptable/finance/caissier | `IAssistantService.BriefingAsync(role)` — réutilise `CommandCenterAsync`/`ManagerDashboardAsync`/`CashierDashboardAsync` (Phase 7) tels quels, enrichis des insights/recommandations filtrés par catégories pertinentes au rôle (table statique `RoleFilters`) ; caissier sans session ouverte → message explicite, jamais un chiffre inventé |
| 11, 16-17 | Recommandations ventes/commerce/services (cross-sell, meilleur créneau, clients inactifs…) | **Backlog explicite** — nécessite une segmentation comportementale absente du modèle actuel ; seules les recommandations stock/créances/trésorerie/budget (§12-13, §18, §20-21) sont livrées cette phase |
| 12-13 | Réapprovisionnement intelligent (historique + délai fournisseur + stock + saisonnalité + stock sécurité → quantité suggérée, toujours une proposition) | **Règle `stock_reorder`** — vélocité sur `periodDays` (60j) × (`lead_time_days` du `product_reorder_rule`, jusqu'ici jamais lu par aucun service, + `safetyDays` configurable) − stock disponible ; confiance dérivée de la profondeur d'historique (mêmes seuils que Phase 42 `IForecastService` : ≥45j haute, ≥14j moyenne, sinon faible) ; saisonnalité — **backlog** (moyenne simple, pas de décomposition saisonnière) |
| 14-15 | Recommandations pharmacie (FEFO/expiration/lot) / restaurant (rentabilité plat/gaspillage) | **Backlog explicite** — le cycle de lots (Phase 24, FEFO/expiration) et le module restaurant (Phase 8) existent déjà pour l'opérationnel ; une couche de recommandation dédiée par secteur n'est pas construite cette phase |
| 12, 51 | Produit dormant/surstock ; scénario §51 (ventes en baisse + stock élevé + marge faible → réduire réappro + réévaluer promotion) | **Règle `stock_dormant`** — aucun mouvement `sale` depuis `dormantDays` (45j) + marge historique (toutes ventes confondues, pas seulement la fenêtre) ; `proposedAction` reprend **littéralement** le texte du scénario §51 ; confiance `insufficient_data` si le produit n'a jamais été vendu (marge non calculable), `high` sinon (marge = fait historique, pas une estimation) |
| 18 | Créances : échéances→risque retard→priorisation→relance suggérée→suivi, aucune décision de crédit automatique | **Règle `receivable_priority`** — classe les clients en retard (`ICreditPortfolioQueries.CollectionsAsync`) par `ancienneté × montant`, filtre `minOverdueAmount`/`topN` configurables ; ne fait jamais que *suggérer* une relance — aucun blocage/déblocage crédit, ça reste `ICreditControl` (Phase 27) |
| 19 | Dettes fournisseurs (échéances/priorité/retards/remises/prévision) | **Backlog explicite** — `IPurchasingQueries`/`ISuspenseService` couvrent déjà le suivi opérationnel (Phase 26/28) ; une règle de recommandation dédiée dettes n'est pas construite (asymétrie assumée avec §18, qui avait un besoin plus net côté relance client) |
| 20 | Trésorerie prévisionnelle (entrées/sorties/solde/échéances/scénarios/seuil minimum/alerte) | **Règle `cashflow_risk`** — délègue à `ITreasuryQueries.LiquidityAsync` (Phase 28), compare le gap projeté à un seuil **configurable** (`intelligence_rule.threshold.minBalance`, §34) plutôt que le seuil fixe (`< 0`) de l'insight `cash_gap` (Phase 42) — les deux coexistent : l'insight détecte l'anomalie, la recommandation propose l'action avec un seuil métier ajustable |
| 21 | Budget (dépassement probable/sous-utilisation/tendance/projection fin période/responsable/action) | **Règle `budget_overrun`** — projette `Actual / fractionÉcoulée` vs `Budget` par compte (réutilise `IBudgetReports.BudgetVsActualAsync`, Phase 29/42) ; confiance croît avec la fraction de l'exercice écoulée ; **responsable par compte — backlog** (`budget_line` ne porte pas de propriétaire nommé) |
| 22-23 | Anomalies (montant/fréquence/horaire/remise/retour/stock/écriture/paiement inhabituels) ; fraude (signaux/score/escalade/revue humaine, aucune accusation automatique) | **Déjà livrés** — Phase 42 `IInsightService` (5 catégories, réexposées sous `/intelligence/anomalies`) et Phase 11 `IFraudDetectionService` (signaux scorés, revue humaine) ; volontairement **non fusionnés** avec les recommandations (domaines distincts : sécurité vs pilotage, même principe déjà acté en Phase 42) |
| 24 | Performance employés (ventes/objectifs/panier/transactions/erreurs caisse/commissions) | **Backlog explicite** — nécessite un modèle de commission/objectif individuel absent du schéma ; `ManagerDashboardResponse.BySalesperson` (Phase 7) couvre déjà un premier niveau, non étendu ici |
| 25-27 | Benchmark interne (entreprise/établissement/période) ; opportunités groupe ; recommandations interentreprises (transfert de stock A→B) | **Backlog explicite** — `GroupDashboardAsync` (Phase 7) et `IGroupControlCenter` (Phase 9) donnent déjà une vue comparative groupe ; une règle de recommandation *dédiée* (détection automatique d'écarts + suggestion de transfert de stock) n'est pas construite cette phase — le scoring (§35) reste par entreprise, pas encore comparatif |
| 28-30 | Assistant conversationnel (question→réponse contextualisée→sources→scope→lien rapport→tâche) ; garde-fous (permissions/scope/pas de secret/confirmation avant action) | **`IAssistantService.QueryAsync`** délègue à `IAskNexora` (Phase 7, déterministe) et **journalise** chaque question dans `assistant_query` (§38) ; garde-fous **déjà assurés en amont** par `RuleBasedAskNexora` (portée RLS systématique, pas d'accès implicite) — aucune action n'est jamais exécutée depuis l'assistant, uniquement des réponses en lecture |
| 31 | Actions depuis recommandation (détails/tâche/brouillon commande/rappel/rapport/approbation/ignorer/reporter/résolu) | `GetAsync` (détails), **`task`** (NEW — comble un manque réel : `group_task` exige un groupe, `onboarding_task` est une checklist fixe) créée en option depuis `AcceptAsync(createTask:true)`, `DismissAsync`/`SnoozeAsync`/`ResolveAsync`. Brouillon de commande, demande d'approbation formelle — **backlog** (l'acceptation reste une décision humaine enregistrée, sans déclenchement automatique d'un autre module) |
| 32 | Actions financières sensibles jamais automatiques sans workflow/permission/approbation | Respecté structurellement : **aucun service de ce module n'écrit jamais** dans `payment`/`journal_entry`/`customer_receivable`/etc. — seules des lignes `recommendation`/`task` sont produites, toute action réelle repasse par les modules Phase 4/6/26/27 avec leurs propres permissions |
| 33 | Boucle d'apprentissage (accepter/rejeter/modifier/résultat positif-négatif/motif/historique) | `AcceptAsync`/`DismissAsync`/**`ResolveAsync`** (`outcome` positive/negative + `outcome_note`) ; historique = la ligne `recommendation` elle-même + `audit_log` (`trg_audit_recommendation`). Amélioration automatique de règle/modèle à partir du feedback — **backlog** (le feedback est capturé et consultable, pas encore reboudé dans un ajustement de seuil) |
| 34 | Règles configurables (seuil/période/entreprise/catégorie/priorité/destinataire/action/escalade/activation/expiration) | **`intelligence_rule`** — nullable `company_id` (système = gabarit, comme `sector_profile` Phase 32), `threshold` jsonb libre par règle, `period_days`, `default_priority`, `recipient_role`, `action_label`, `escalate_after_days`, `enabled`, `valid_from`/`expires_at` ; `IIntelligenceRuleService.SaveAsync` **upsert par clé** (l'entreprise ne fait jamais que surcharger sa propre clé, jamais une valeur système) |
| 35 | Scoring (stock/risque créance/client/produit/fournisseur/entreprise, variables expliquées) | **`IScoringService`** — 3 scoreurs concrets : `stock_coverage` (jours de couverture), `credit_risk` (pondération ancienneté/montant/litiges/blocage), `supplier_reliability` (taux de réception à temps + taux de factures sans écart de rapprochement, premier usage de ces deux signaux combinés). Score entreprise (comparatif groupe) — **backlog**, lié à §25-27 |
| 36 | Prévision (horizon/historique minimum/saisonnalité/tendance/scénarios/intervalle-risque/date calcul/version modèle) | **Déjà livré** (Phase 42 `IForecastService`), réexposé sous `/intelligence/forecasts` sans duplication |
| 37 | Qualité des recommandations (données suffisantes/fraîcheur/complétude/biais/taux d'acceptation/succès/faux positifs-négatifs/réévaluation) | Données suffisantes → `confidence_level`/`confidence_reason` par recommandation ; taux d'acceptation/succès — **calculables a posteriori** depuis `recommendation.status`/`outcome` (pas encore agrégés dans un tableau de bord dédié, **backlog**) |
| 38 | Observabilité IA (id/modèle/version/entrées/sortie/confiance/utilisateur/action/résultat/erreur) | **`IIntelligenceAuditQuery`** lit `audit_log` (immuable, Phase 11) filtré aux tables du module — aucune table dupliquée, l'audit reste la source unique ; `assistant_query` journalise question/intent/réponse/sources/confiance pour l'assistant |
| 39 | Gouvernance (propriétaire/version/date validation/périmètre/critères/tests/approbation/retrait) | **`IntelligenceModelCatalog`** (statique, comme `KpiCatalog` Phase 7) documente les 5 règles + 3 scoreurs (clé/propriétaire/version/entrées) ; approbation/retrait d'une règle *entreprise* — `intelligence_rule.approved_by`/`approved_at`/`version` existent en base, **non encore branchés sur un workflow d'approbation** (§39 « approbation » — backlog, la colonne existe pour l'accueillir) |
| 40 | Protection des données (minimisation/accès par rôle/masquage/pas d'entraînement externe/rétention/audit/séparation tenants) | RLS entreprise sur les 5 nouvelles tables ; permissions dédiées (`intelligence.*`) ; aucun appel externe, aucun modèle entraîné — tout est SQL déterministe ; rétention — **héritée** de la politique générale (Phase 41 `IRetentionPurgeService`), pas d'ajout spécifique |
| 41 | Données externes (sources autorisées/qualité/fraîcheur/fallback) | **Non applicable** — aucune source externe utilisée cette phase (tout dérive de données NEXORA déjà validées) |
| 42-44 | Mode local/cloud/hybride | **Hors périmètre applicatif** — architecture déjà mono-déploiement (Phase 16/37) ; aucun calcul n'est déporté, tout tourne dans le même processus API que le reste de NEXORA ONE |
| 45 | Centre d'intelligence (nouvelles recommandations, priorité, confiance, entreprise, catégorie, date, statut, actions) | **`IntelligencePage.tsx`** — onglet Recommandations (tri implicite par `priorityScore`), avec confiance/catégorie/statut/actions visibles ; onglets Boucle d'apprentissage, Scores, Tâches, Gouvernance, Règles |
| 46 | Priorisation (impact × urgence × confiance = priorité, formule configurable mais explicable) | `recommendation.priority_score` — **colonne générée** `impact_score * urgency_score * GREATEST(confidence_score,1)` ; formule fixe (pas encore configurable par entreprise) mais **entièrement explicable** (les 3 facteurs sont eux-mêmes visibles sur chaque recommandation) |
| 47 | Notifications intelligentes (résumé/regroupement/priorité/escalade/silence/canal) | **Backlog assumé** — la génération de recommandations ne notifie pas automatiquement (évite le spam évoqué au §47 sans construire le regroupement/digest promis) ; les notifications existantes (Phase 34) restent utilisées seulement pour l'assignation explicite d'un insight (Phase 42, inchangée) |
| 48 | API principales | Toutes servies — voir § Backend/API |
| 49 | Événements | Portés par les colonnes de statut (`recommendation.status`/`outcome`, `task.status`) + `audit_log` (trigger) + `notification` (accepter avec tâche) — cohérent avec le style déjà en place (pas de bus d'évènements séparé, comme Phases 30/34/39/41/42) |
| 50 | Tests | `IntelligenceEngineTests` (6) + `IntelligenceEndpointsTests` (3) — cf. ci-dessous |
| 51 | Scénario complet | Directement reproduit par le test `Generation_stock_reorder_and_dormant_explain_confidence_and_match_scenario` — `proposedAction` égale mot pour mot le texte du scénario |
| 52 | Critères de sortie | Règles/statistique/scoring/recommandations/confiance/explication/assistants (5 rôles)/créances/trésorerie/budget/anomalies/risque/gouvernance/protection données/centre d'intelligence/priorisation/API/événements/tests **livrés** ; ventes/pharmacie/restaurant/commerce/services/benchmark/opportunités groupe/interentreprises/performance employés/actions sensibles avancées/local-cloud-hybride/notifications intelligentes : **backlog explicite**, détaillé ci-dessus et en fin de document |
| 53 | Étape suivante — Phase 44 (mobile, terrain, multi-appareils) | hors périmètre |

## `V056` — contenu (5 tables neuves, 4 permissions, 1 job)

- **`recommendation`** (NEW, RLS entreprise) — `priority_score` **généré** (`impact × urgence ×
  GREATEST(confiance,1)`, §46), `UNIQUE NULLS NOT DISTINCT (company_id, category, drill_path,
  period_start)` — même dédup qu'`insight` (Phase 42) ; cycle `new → accepted|dismissed|snoozed
  → resolved` avec `outcome` (boucle d'apprentissage §33).
- **`intelligence_rule`** (NEW, `company_id` nullable = gabarit système comme `sector_profile`) —
  5 règles système amorcées (seuils par défaut documentés dans le code).
- **`entity_score`** (NEW, RLS entreprise) — `UNIQUE (company_id, entity_type, entity_id,
  score_key)`, upsert à chaque recalcul.
- **`assistant_query`** (NEW, RLS entreprise) — journal d'ajout seul (comme `report_execution`,
  non audité par trigger : append-only).
- **`task`** (NEW, RLS entreprise) — comble un manque confirmé par l'audit du code existant :
  `group_task` (V028) exige un groupe, `onboarding_task` (V052) est une checklist fixe fermée.
- `trg_audit_*` sur `recommendation`/`intelligence_rule`/`entity_score`/`task` (pas sur
  `assistant_query`, journal d'ajout seul). Permissions **`intelligence.{view,manage_rules,
  act_on_recommendation,use_assistant}`** ; job planifié **`intelligence.generate_recommendations`**
  (par entreprise, calcule aussi les scores dans la même passe).
- **230 tables (+5), migrations V001–V056.**

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Intelligence/IntelligenceContracts.cs` |
| Application | `Nexora.Application/Intelligence/IIntelligence.cs` — `IRecommendationService`, `IIntelligenceRuleService`, `IScoringService`, `IAssistantService`, `ITaskService`, `IIntelligenceAuditQuery`, `IntelligenceModelCatalog` (statique) ; `PermissionCatalog.Intelligence` |
| Infrastructure | `Intelligence/{RecommendationService,ScoringService,IntelligenceRuleService,AssistantService,TaskService,IntelligenceAuditQuery}.cs` ; `SchedulerRunner` += 1 job (`intelligence.generate_recommendations`, calcule recommandations **et** scores) |
| API | `Nexora.Api/Controllers/IntelligenceController.cs` — `/api/v1/intelligence/*` + `/api/v1/assistant/{briefing,query}` |

### Points de conception

- **Le briefing par rôle ne redérive rien** — `IAssistantService.BriefingAsync` appelle
  directement `CommandCenterAsync`/`ManagerDashboardAsync`/`CashierDashboardAsync` (Phase 7) et
  sérialise la réponse telle quelle dans `AssistantBriefingDto.Dashboard` (un `JsonElement`
  générique) ; seuls les insights/recommandations affichés varient par rôle (table statique de
  catégories pertinentes), jamais une deuxième requête d'agrégation.
- **`cashflow_risk` coexiste avec l'insight `cash_gap` sans le dupliquer** — l'insight (Phase 42)
  détecte au seuil fixe `< 0` ; la recommandation compare au seuil **configurable**
  (`intelligence_rule.threshold.minBalance`) et ajoute l'action proposée + la boucle
  d'apprentissage. Les deux lisent la même `ITreasuryQueries.LiquidityAsync`, jamais deux calculs.
- **`product_reorder_rule.lead_time_days` était une colonne morte depuis la Phase 5/V024** —
  jamais lue par aucune requête (confirmé par l'audit du code existant) ; la règle `stock_reorder`
  est le premier consommateur réel de cette donnée.
- **Upsert par clé, jamais par id, pour les règles configurables** — `SaveIntelligenceRuleRequest`
  ne porte pas d'`Id` : l'identité d'une règle est `(entreprise, key)`, comme `sector_profile`
  (Phase 32). Le service ne crée jamais de ligne `company_id IS NULL` (seule la migration en
  amorce) — la lecture accepte les deux, la règle système reste visible même après une surcharge.
- **`GENERATED ALWAYS AS` pour la priorité** — `priority_score` est une colonne calculée en base,
  jamais recalculée en C#, pour garantir qu'elle reste toujours cohérente avec les 3 facteurs
  qu'elle affiche (§46 « rester explicable »).
- **`GREATEST(confidence_score, 1)`** — évite qu'une confiance « données insuffisantes »
  (`confidence_score = 0`) mette la priorité entière à zéro : le manque de données abaisse la
  confiance affichée sans supprimer la recommandation elle-même de la liste triée.
- **Tests d'intégration insensibles à l'ordre d'exécution** — `Generation_cashflow_risk_…` purge
  explicitement toute recommandation `cashflow_risk` préexistante avant de s'exécuter : la suite
  partage une base clonée unique par run (`FinanceEngineFixture`), et une règle système par défaut
  aurait pu déjà se déclencher depuis un test précédent de la même classe.

## Tests d'intégration — `IntelligenceEngineTests` (6, `[Collection("finance")]`, entreprise c2)

| Test | § | Vérifie |
|---|---|---|
| `Generation_stock_reorder_and_dormant_explain_confidence_and_match_scenario` | 12-13, 51 | quantité suggérée > 0 avec confiance « medium » (20j d'historique) ; `stock_dormant` reprend mot pour mot le texte du scénario §51, confiance « high » (marge = fait historique) ; second passage → 0 (dédup) |
| `Generation_receivable_priority_ranks_overdue_customer_without_credit_decision` | 18 | client en retard → recommandation avec le bon montant, confiance « high », aucune décision de crédit automatique |
| `Generation_cashflow_risk_fires_once_rule_threshold_is_overridden` | 20, 34 | seuil système par défaut surchargé par l'entreprise → la recommandation reflète le seuil surchargé (pas le gabarit) ; le gabarit système reste visible après suppression de la surcharge |
| `Recommendation_lifecycle_accept_creates_task_dismiss_snooze_and_resolve_outcome` | 31, 33 | acceptation avec `createTask:true` crée une tâche assignée ; résolution positive enregistre l'`outcome` ; une recommandation résolue ne peut plus être ignorée |
| `Scoring_computes_explainable_stock_and_credit_scores` | 35 | score de couverture de stock calculé avec ses variables ; score de risque créance atteint le niveau « high » attendu selon la formule pondérée |
| `Assistant_briefing_reuses_dashboards_per_role_and_query_is_logged` | 6-10, 28-30, 38 | briefing direction/manager réutilisent bien `CommandCenterResponse`/`ManagerDashboardResponse` (sérialisés tels quels) ; caissier sans session → message explicite ; une question posée est journalisée dans `assistant_query` |

## Tests API — `IntelligenceEndpointsTests` (3)

| Test | Vérifie |
|---|---|
| `Intelligence_routes_require_authentication` | 4 routes `/api/v1/*` sans jeton → 401 |
| `Read_only_intelligence_endpoints_are_wired_over_http` | 8 endpoints de lecture → 200, catalogue de gouvernance non vide |
| `Assistant_briefing_and_query_are_wired_over_http` | briefing direction → 200 avec le bon rôle ; question posée → 200 avec un intent |

## Frontend

`src/intelligence/intelligenceApi.ts` (types + fonctions) + `src/intelligence/
IntelligenceSections.tsx` (7 sections) branchées dans **`IntelligencePage.tsx`** (nouvelle page,
route `/intelligence`, nav « Pilotage », icône `Bulb` ajoutée à `shared/icons.tsx`, permission
`intelligence.view` — ou `intelligence.use_assistant` seul pour l'onglet Assistant) :

- **Recommandations** — liste triée par priorité, accepter (avec option « créer une tâche »)/
  reporter/ignorer, gardées par `intelligence.act_on_recommendation` ;
- **Boucle d'apprentissage** — recommandations acceptées en attente d'un résultat positif/négatif ;
- **Scores** — tableau produit/client/fournisseur avec niveau de risque ;
- **Tâches** — liste + marquer terminée ;
- **Gouvernance** — catalogue des règles/scoreurs en production (propriétaire/version) ;
- **Règles** — règles système vs surcharges entreprise, activer/retirer une surcharge (gardé par
  `intelligence.manage_rules`) ;
- **Assistant** — sélecteur de rôle (résumé + actions recommandées + points d'attention réutilisant
  les mêmes données) et question libre en langage naturel avec sources visibles.

Vitest `intelligenceApi.test.ts` (5) → **81 tests frontend**.

## Manques assumés (backlog)

- Recommandations ventes/commerce/services/pharmacie/restaurant sectorielles (§11, §14-17) —
  seules stock/créances/trésorerie/budget sont couvertes cette phase
- Recommandations interentreprises (transfert de stock A→B) et benchmark/opportunités groupe
  (§25-27) — le scoring reste par entreprise, pas encore comparatif
- Performance employés individuelle (§24) — pas de modèle de commission/objectif nommé dans le schéma
- Score entreprise comparatif (§35, lié à §25-27)
- Formule de priorisation configurable par entreprise (§46) — actuellement fixe (impact × urgence ×
  confiance), mais entièrement explicable
- Notifications intelligentes groupées/digest (§47) — la génération ne notifie pas automatiquement,
  volontairement, pour éviter le spam évoqué au même paragraphe
- Workflow d'approbation/retrait formel d'une règle entreprise (§39) — les colonnes
  `approved_by`/`approved_at`/`version` existent, pas encore branchées sur un circuit d'approbation
- Ajustement automatique de règle/modèle à partir du feedback (§33) — le feedback est capturé et
  consultable, pas encore reboudé dans un ajustement de seuil
- Tableau de bord qualité des recommandations (taux d'acceptation/succès/faux positifs, §37) —
  calculable depuis `recommendation.status`/`outcome`, pas encore agrégé
- Brouillon de commande / demande d'approbation formelle depuis une recommandation (§31) —
  l'acceptation reste une décision humaine enregistrée sans déclencher un autre module
- Classification arbitraire (§3) et sources de données externes (§41) — aucun cas d'usage concret
  identifié cette phase
- Modes local/cloud/hybride distincts (§42-44) — hors périmètre, architecture mono-déploiement
