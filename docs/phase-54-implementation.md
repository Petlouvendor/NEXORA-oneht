# Phase 54 — Intelligence & Business Decision Engine — implémentation

**Statut : livré et vérifié.** Migration `V061` (0 table neuve, 4 colonnes sur `recommendation`,
2 catégories de règle CHECK étendues, 4 règles système amorcées). 6 nouveaux tests d'intégration
(`IntelligenceEngineTests`, total 12), 1 nouveau test API (`IntelligenceEndpointsTests`, total 4),
2 nouveaux tests frontend (`intelligenceApi.test.ts`, total 7).
**369 tests backend** verts (18 unit + 47 API + 304 intégration), 0 warning, vérifiés après un
rebuild complet de la base (`V001`→`V061`, 249 tables — inchangé, cette phase n'ajoute aucune
table). 105 tests frontend. `tsc` vert.

**Constat préalable, déterminant pour le périmètre de cette phase :** le cahier des charges de la
Phase 54 redemande, section par section, un système déjà livré à la Phase 43 (V056) — moteur de
règles déterministe en C#, recommandations expliquées et persistées, boucle d'apprentissage,
scoring explicable, séparation stricte logique métier/IA (aucun LLM, tout est SQL déterministe),
isolation multi-entreprise par RLS, assistant conversationnel respectant les permissions. Plutôt
que de reconstruire ce socle, cette phase a d'abord fait l'inventaire précis de ce qui existait
(catalogue `IntelligenceModelCatalog`, schéma `V056`, doc `phase-43-implementation.md`) pour
identifier le **delta réel** : les 3 domaines de règles que la Phase 43 documentait déjà
explicitement en backlog (ventes/clients/fournisseurs, §9.2/§9.4/§9.5), une priorité **catégorielle**
explicite (CRITIQUE/IMPORTANT/À SURVEILLER/OPPORTUNITÉ, §7) au-delà du score numérique existant,
une action « modifier » distincte d'accepter/ignorer (§16, §19), une expiration effective des
recommandations (§6, §21 — la colonne n'existait pas), et une vue générale dédiée agrégeant par
priorité (§5, §20).

### Socle réutilisé (Phase 43, non reconstruit)

- **`recommendation`/`IRecommendationService`** — cycle de vie (`ListAsync`/`GetAsync`/`AcceptAsync`/
  `DismissAsync`/`SnoozeAsync`/`ResolveAsync`), `priority_score` généré (impact × urgence ×
  confiance, §46), déduplication par `(company_id, category, drill_path, period_start)`,
  explicabilité (`why`/`evidence`/`impact_estimate`/`proposed_action`/`expected_result`/
  `confidence_level`/`confidence_reason`) — **étendus**, jamais dupliqués
- **`intelligence_rule`/`IIntelligenceRuleService`** — seuils configurables par entreprise, gabarit
  système (`company_id` nullable), upsert par clé — 4 nouvelles règles amorcées de la même façon
  que les 5 existantes, aucun nouveau mécanisme
- **`IInsightService`** (Phase 42, réexposé Phase 43) — anomalies, désormais aussi référencées
  (comptées) par la nouvelle vue générale sans redérivation
- **`IScoringService`/`ITaskService`/`IAssistantService`/`IIntelligenceAuditQuery`** — non touchés,
  toujours en production tels quels
- **`SchedulerRunner`** (job `intelligence.generate_recommendations`) — non touché : l'expiration
  (§6, §21) est intégrée à ce job existant plutôt que d'en créer un second

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-3 | Principe (analyser/expliquer/recommander, jamais décider) ; boucle de décision ; architecture fonctionnelle | **Hérité et validé** (Phase 43 §2) — non retouché |
| 4-5 | Module Intelligence au menu (11 vues) ; Dashboard (état général, comptes par priorité, actions prioritaires, opportunités, résumé tendances) | Module déjà au menu (Phase 43, nav « Pilotage »). **Nouveauté** : onglet **Vue générale**, `GET /intelligence/dashboard` — comptes par `priority_level`, 5 actions prioritaires, 5 opportunités, compte d'anomalies ouvertes (délégué à `IInsightService`, jamais recalculé). Résumé des tendances ventes/stock/finance — **déjà disponible** via `/intelligence/forecasts` et l'assistant par rôle (Phase 43 §6-10), non dupliqué dans le dashboard |
| 6-7 | Champs recommendations (dont `expires_at`) ; statuts (dont MODIFIED/EXPIRED) ; priorité catégorielle CRITIQUE/IMPORTANT/À SURVEILLER/OPPORTUNITÉ | **`expires_at`** (NEW, timestamptz, 30 jours par défaut) ; **statuts étendus** avec `modified`/`expired` (les 5 valeurs Phase 43 restent, aucun renommage rétroactif — `dismissed` reste `dismissed`, pas renommé `ignored`, pour ne rien casser) ; **`nature`** (risk/opportunity) + **`priority_level`** — colonne **générée**, comme `priority_score` (même principe Phase 43 §46 : jamais recalculée côté client), dérivée de `nature` et des 3 facteurs de score |
| 8-9 | Rules Engine déterministe en C# ; bibliothèque de règles (stock ×10, ventes ×8, finance ×7, clients ×6, fournisseurs ×5 = 36) | **Moteur hérité et validé** (Phase 43, 5 règles SQL bornées). **4 règles neuves**, une par domaine explicitement backlogué : `sales_spike`/`sales_decline` (§9.2), `customer_inactive` (§9.4), `supplier_price_increase` (§9.5) — 9 règles au total. Les 27 règles restantes de la bibliothèque cible (stock ×8 restantes, ventes ×6, finance ×4, clients ×5, fournisseurs ×4) — **backlog explicite**, détaillé ci-dessous ; ce choix priorise **une règle représentative et solide par domaine manquant** plutôt que 36 règles superficielles |
| 10 | Explicabilité (problème→données→calcul→conclusion→action) | **Hérité** (Phase 43 §4-5) — les 4 nouvelles règles suivent exactement le même contrat (`why`/`evidence`/`proposed_action`/`expected_result`) |
| 11 | Analytics Engine (CA jour/semaine/mois, croissance, rotation, marge, cash-flow…) | **Hérité** (Phase 7 `IKpiService`, Phase 42 `insight`/`forecast`) — non reconstruit ; les nouvelles règles ventes réutilisent une comparaison de fenêtres glissantes (§37 « données suffisantes » — historique minimum vérifié avant toute conclusion) |
| 12 | Prévisions (ventes/demande/stock, 7/30 jours, confiance, saisonnalité) | **Hérité** (Phase 42 `IForecastService`, réexposé Phase 43) — non retouché |
| 13 | Détection d'anomalies | **Hérité** (Phase 42 `IInsightService`) — non retouché, seulement référencé par le nouveau dashboard |
| 14-15 | Assistant en langage naturel, permissions respectées ; séparation logique métier/IA (aucun LLM pour les calculs) | **Hérité et confirmé** (Phase 43 §28-30) — `IAssistantService` délègue à `IAskNexora` (déterministe). Les 4 nouvelles règles respectent la même discipline : agrégats SQL directs, aucune inférence statistique avancée |
| 16, 19 | Actions (accepter/modifier/ignorer) ; statuts incluant MODIFIED | **`ModifyAsync`** (NEW) — `POST /intelligence/recommendations/{id}/modify` : enregistre `override_value` (la valeur ajustée) **sans jamais écraser** `impact_estimate` (la proposition d'origine) — les deux restent visibles, conforme au §18 (valeur initiale ET valeur modifiée) |
| 17 | Apprentissage du contexte entreprise (historique, saisonnalité…) | **Hérité** — `stock_reorder`/`stock_dormant` (Phase 43) et les 4 nouvelles règles lisent déjà l'historique réel de l'entreprise ; saisonnalité explicite (§17 exemple décembre) — **backlog**, comme déjà noté en Phase 43 (moyenne simple, pas de décomposition saisonnière) |
| 18 | Feedback (id/utilisateur/action/valeur initiale/valeur modifiée/motif/date) | **`ModifyAsync`** journalise `decided_by`/`decided_at`/`decision_reason` (valeur initiale = `impact_estimate`, valeur modifiée = `override_value`) ; la boucle d'apprentissage positive/négative reste `ResolveAsync` (Phase 43, inchangé) |
| 20 | API (`GET /dashboard`, `GET/POST recommendations/*`, `GET stock/sales/finance`, `GET anomalies/forecasts`, `POST assistant`) | `GET /intelligence/dashboard` (NEW), `POST /intelligence/recommendations/{id}/modify` (NEW) ; le reste correspond déjà aux routes Phase 43 (`/intelligence/recommendations*`, `/intelligence/anomalies`, `/intelligence/forecasts`, `/assistant/query`) — **`GET /intelligence/{stock,sales,finance}`** dédiées n'existent pas comme routes séparées : ces axes sont couverts par les règles + les dashboards Phase 7 déjà réexposés dans le briefing par rôle, pour éviter une troisième forme de "vue stock" à maintenir |
| 21 | Traitements automatiques (analyse quotidienne, recalcul, génération **et expiration**) | `GenerateAsync` (job `intelligence.generate_recommendations`, inchangé) **expire d'abord** les recommandations `new` échues avant de générer les nouvelles — jamais l'inverse, pour ne pas qu'une recommandation fraîchement créée puisse expirer dans la même passe |
| 22-24 | Architecture technique, structure de dossiers, services C# recommandés | **Conforme à l'existant** — `Nexora.Application/Intelligence`, `Nexora.Infrastructure/Intelligence`, `Nexora.Contracts/Intelligence` (Phase 43) ; les 4 nouvelles règles vivent dans le même `SqlRecommendationService`, pas un service séparé par domaine (cohérent avec le choix Phase 43 de ne pas multiplier les classes par catégorie) |
| 25-26 | Sécurité multi-entreprise, permissions `INTELLIGENCE_*`, audit | **Hérité et validé** — RLS entreprise sur `recommendation` (inchangée), permissions `intelligence.{view,manage_rules,act_on_recommendation,use_assistant}` (Phase 43) réutilisées telles quelles pour `ModifyAsync`/`DashboardAsync`, aucune permission nouvelle ; `trg_audit_recommendation` (Phase 43) couvre aussi les nouvelles colonnes automatiquement |
| 27 | Design UI (Dark Premium, priorités visibles, cartes réutilisables) | Onglet **Vue générale** avec pastilles de compte par priorité (rouge/orange/bleu/vert) ; badge de priorité catégorielle sur chaque ligne de la liste des recommandations (remplace l'ancien badge de score numérique brut) |
| 28 | Paramètres Intelligence (seuils configurables par entreprise) | **Hérité** (`intelligence_rule.threshold` jsonb, Phase 43 §34) — les 4 nouvelles règles suivent le même mécanisme de surcharge par entreprise |
| 29-30 | Roadmap 54.1-54.10 ; MVP obligatoire | Le MVP (§30 : stock faible/rupture/surstock/quantité recommandée, ventes hausse/baisse/produits performants, finance CA/marge/dépenses, dashboard, actions, explication, assistant) est **déjà couvert à ≥ 80 %** par le cumul Phase 43 + cette phase — seuls « produits performants » (classement produit) et « finance CA/dépenses en détail » restent partiellement couverts par les dashboards Phase 7 plutôt que par une règle de recommandation dédiée |
| 31 | Critères d'acceptation (10 points) | Tous vérifiés par les tests — voir tableau ci-dessous ; le point 2 (« hausse significative → opportunité ») est **exactement** le nouveau test `Generation_sales_spike_is_an_opportunity_not_a_risk` |
| 32 | Livrables du programmeur | Migration, entités, Rules Engine, Analytics Engine (hérité), Dashboard React, API documentée, statuts, actions, audit, jobs, anomalies (hérité), prévisions (hérité), assistant (hérité), tests, documentation — **tous livrés** |
| 33-34 | Règle fondamentale ; vision finale | Respectées structurellement — aucune action financière automatique, toute recommandation reste une proposition validée par un humain |

## `V061` — contenu (0 table neuve, 4 colonnes, 4 règles amorcées)

- **`recommendation`** (Phase 43, ALTER) — `+ nature` (risk/opportunity, CHECK) ; `+ priority_level`
  (colonne **générée**, dérivée de `nature` et de `impact_score × urgency_score ×
  GREATEST(confidence_score,1)` — Postgres interdisant qu'une colonne générée en référence une
  autre, la formule de `priority_score` V056 est réécrite ici plutôt que référencée) ; `+ expires_at`
  (timestamptz, §6/§21) ; `+ override_value` (jsonb, §16/§18/§19) ; `status` CHECK étendu
  (`+ modified, + expired`) ; `category` CHECK étendu (`+ sales_spike, + sales_decline,
  + customer_inactive, + supplier_price_increase`).
- Nouvel index `recommendation_priority_level_idx (company_id, priority_level, status)`.
- 4 nouvelles lignes système dans `intelligence_rule` (gabarit `company_id IS NULL`, comme les 5
  existantes) : `sales_spike`, `sales_decline`, `customer_inactive`, `supplier_price_increase`.
- Aucune nouvelle permission (réutilise `intelligence.{view,act_on_recommendation}`).
- **249 tables (inchangé), 210 RLS (inchangé)**, 61 migrations (V001–V061).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Intelligence/IntelligenceContracts.cs` (étendu : `RecommendationDto` +4 champs, `ModifyRecommendationRequest`, `IntelligenceDashboardDto`) |
| Application | `Nexora.Application/Intelligence/IIntelligence.cs` (étendu : `IRecommendationService.ModifyAsync`/`DashboardAsync`, `IntelligenceModelCatalog` +4 entrées) |
| Infrastructure | `Intelligence/RecommendationService.cs` (étendu : 4 nouvelles règles, `ModifyAsync`, `DashboardAsync`, `ExpireDueAsync`) |
| API | `Nexora.Api/Controllers/IntelligenceController.cs` (étendu : `POST recommendations/{id}/modify`, `GET dashboard`) |

### Points de conception

- **Priorité catégorielle dérivée, jamais un second calcul (§7, §46)** — `priority_level` est une
  colonne `GENERATED ALWAYS AS`, exactement comme `priority_score` (Phase 43) : une opportunité
  n'est **jamais** comparée sur l'échelle de gravité d'un risque (`nature = 'opportunity'` court-circuite
  le calcul de score), les deux axes restent distincts comme le veut le §7 (une opportunité n'est
  « pas plus urgente qu'une rupture », c'est un autre type de signal).
- **`sales_spike`/`sales_decline` : la seule paire de règles agrégée à l'échelle de l'entreprise
  entière** (les 7 autres règles, Phase 43 comme cette phase, restent bornées à un produit/client/
  fournisseur/compte précis) — ceci a directement dicté la stratégie de test : ces deux règles
  exigent une entreprise fraîchement créée par test (`MakeCompany()`) pour éviter toute pollution
  par les ventes que d'autres tests insèrent déjà dans l'entreprise partagée de la suite.
- **`ExpireDueAsync` s'exécute avant la génération, jamais après** — dans `GenerateAsync`, pour
  qu'une recommandation qui vient d'être (re)créée dans la même passe ne puisse jamais être
  expirée immédiatement par erreur ; seul le statut `new` peut expirer, une décision humaine
  (`accepted`/`modified`/`dismissed`/`snoozed`/`resolved`) ne s'efface jamais toute seule.
- **`ModifyAsync` ne touche jamais `impact_estimate`** — la proposition d'origine de la règle reste
  intacte en base pour toujours pouvoir comparer ce qui a été proposé vs ce qui a été retenu (§18) ;
  `override_value` est une colonne distincte, jamais une réécriture in place.
- **Dashboard = agrégation, jamais un second calcul (§5, §20)** — `DashboardAsync` interroge
  directement `recommendation` (comptes par `priority_level`, statut `new`) et délègue à
  `IInsightService.ListAsync("open")` pour le compte d'anomalies plutôt que de dupliquer sa logique.
- **`GET /intelligence/{stock,sales,finance}` du §20 non construites comme routes dédiées** — choix
  assumé : les dashboards par rôle (Phase 7, réexposés par l'assistant Phase 43) couvrent déjà ces
  axes ; ajouter une troisième forme de "vue stock/ventes/finance" aurait dupliqué une source de
  vérité existante plutôt que de combler un vrai manque.

## Tests d'intégration — 6 nouveaux dans `IntelligenceEngineTests` (total 12, entreprise c2 sauf mention)

| Test | § | Vérifie |
|---|---|---|
| `Generation_sales_spike_is_an_opportunity_not_a_risk` | 7, 9.2 | entreprise dédiée ; hausse de CA moyen quotidien → `sales_spike`, `nature=opportunity`, `priorityLevel=opportunity` ; aucune `sales_decline` en parallèle |
| `Generation_sales_decline_flags_a_risk_with_high_priority` | 7, 9.2 | baisse sévère → `sales_decline`, `nature=risk`, priorité critique/importante |
| `Generation_customer_inactive_flags_a_previously_active_customer_only` | 9.4 | client avec historique + silence > seuil → signalé ; client actif récemment → jamais signalé |
| `Generation_supplier_price_increase_compares_recent_to_prior_orders` | 9.5 | hausse de prix moyen par couple produit/fournisseur détectée avec les bonnes valeurs |
| `Modify_keeps_the_original_proposal_and_expired_recommendations_leave_new_status` | 6, 16, 18, 19, 21 | modifier conserve `impact_estimate` d'origine et journalise `override_value` ; une recommandation `new` échue expire, une `modified` ne l'est jamais |
| `Dashboard_counts_new_recommendations_by_priority_level` | 5, 20 | comptes exacts par niveau de priorité ; actions prioritaires et opportunités correctement séparées |

## Tests API — 1 nouveau dans `IntelligenceEndpointsTests` (total 4)

| Test | Vérifie |
|---|---|
| `Dashboard_is_wired_and_modify_validates_the_target_recommendation` | `GET /intelligence/dashboard` → 200 avec la forme attendue ; `POST .../modify` sur un id inexistant → 404 (câblage bout en bout sans mutation persistée) |

## Frontend

`src/intelligence/intelligenceApi.ts` étendu (`RecommendationDto` +4 champs, `IntelligenceDashboard`,
`modifyRecommendation`, `getIntelligenceDashboard`) + `src/intelligence/IntelligenceSections.tsx`
(nouvelle section `IntelligenceDashboardSection`, badge de priorité catégorielle sur
`RecommendationsSection`, bouton **Modifier**) branché dans `IntelligencePage.tsx` (nouvel onglet
**Vue générale**, devenu l'onglet par défaut à l'ouverture de la page).

Vitest `intelligenceApi.test.ts` (+2) → **105 tests frontend**.

## Manques assumés (backlog)

- 27 des 36 règles de la bibliothèque cible (§9) — stock : rotation forte/faible, hausse/baisse de
  demande isolée, stock de sécurité insuffisant comme règle dédiée (`stock_reorder` couvre déjà la
  quantité recommandée) ; ventes : produit à forte croissance/déclin **au niveau produit** (les
  règles livrées sont à l'échelle entreprise), comparaison période/année précédente, produits les
  plus vendus/rentables ; finance : baisse de marge, dépenses inhabituelles, augmentation des achats,
  baisse du bénéfice (au-delà de `budget_overrun`/`cashflow_risk` déjà livrés) ; clients : dette
  importante/retard de paiement (déjà couverts par `receivable_priority`), client très actif,
  évolution du panier moyen ; fournisseurs : délai de livraison élevé, fournisseur principal,
  risque fournisseur à surveiller au-delà du prix
- `GET /intelligence/{stock,sales,finance}` comme routes API dédiées (§20) — couvertes indirectement
  par les dashboards par rôle existants (Phase 7/43), jamais construites comme vues séparées
- Statuts `VIEWED`/`COMPLETED` (§19) — `modified`/`expired` ajoutés, ces deux-là jugés de moindre
  valeur immédiate (« vu » n'a pas d'effet métier distinct de `new`, « terminé » recoupe largement
  `resolved` avec `outcome=positive`)
- Saisonnalité explicite dans l'apprentissage du contexte (§17, exemple décembre) — moyenne simple
  sur fenêtre glissante, pas de décomposition saisonnière (identique au backlog déjà noté Phase 43)
- Ajustement automatique de seuil de règle à partir du feedback (§18) — capturé et consultable, pas
  encore reboudé dans un ajustement de seuil (identique au backlog Phase 43)
- Tableau de bord qualité des recommandations (taux d'acceptation/succès par catégorie) — calculable
  depuis `status`/`outcome`, pas encore agrégé (identique au backlog Phase 43)
