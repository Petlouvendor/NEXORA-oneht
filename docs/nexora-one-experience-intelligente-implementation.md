# NEXORA ONE — Expérience intelligente — implémentation

**Statut : livré et vérifié (sections 1 à 8 du [brief](nexora-one-experience-intelligente.md)).** Aucune migration
(le schéma reste à V078). Backend : 5 tests d'intégration (`ExperienceTests`) + 2 tests API
(`ExperienceEndpointsTests`) ; suite complète **45 unitaires + 185 API + 403 intégration** verte, 0 avertissement.
Frontend : 3 nouveaux fichiers de tests (15 tests), **183 tests** verts, `tsc` vert, build Vite OK. Parcours
réel vérifié dans un navigateur headless (connexion, dashboard, Ctrl+K, NEXORA AI, analyse d'une
recommandation) sur la base de recette : 0 erreur console, 0 requête en échec.

## Constat préalable

Le brief redemande en grande partie un socle déjà livré : design system Dark Premium + `#00F5D4` (Phase 13/40),
palette Ctrl+K (Phase 13), Command Center (Phase 7), recommandations expliquées et priorisées (Phases 43/54),
assistant déterministe Ask Nexora qui répondait déjà à « Pourquoi mes bénéfices ont diminué ? » (Phase 7,
décomposition volume / taux de marge / dépenses). Ce socle est **conservé et étendu**, pas reconstruit. Le delta
réel était :

| § | Manque constaté | Livré |
|---|---|---|
| 1 | THE NEXT GROUP et son slogan absents | Connexion, barre latérale, design system §24 |
| 3 | Palette v1 à teinte verte ; pas de profondeur/verre | Palette v2 exacte du brief (neutres froids, bordures translucides), tokens d'élévation, verre, signature IA ; mode clair aligné |
| 4 | Pas de composants d'intelligence réutilisables | `src/shared/intel.tsx` : `SectionHeader`, `HeroKpi`/`Sparkline`/`Delta`, `SignalCount`, `RecommendationCard`, `ActivityTimeline`, `BarList`, `ForecastChart` ; design system **2.0.0** (30 composants) |
| 5 | Menu non structuré selon les 13 modules ; Ctrl+K ne cherchait que des **pages** | Menu Dashboard / Commerce / Opérations / Finance / RH / Pilotage / Paramètres (routes `/suppliers`, `/automation` ajoutées), filtré par permission ; **recherche globale serveur** (produit, client, fournisseur, employé, facture, vente) + actions + paramètres + « Demander à NEXORA AI » |
| 6 | Command Center = mur de KPI + alertes | Accueil personnalisé, 4 KPI héros avec variation, bandeau Intelligence, top performance, risques par domaine, **fil d'activité**, prévisions |
| 7 | Recommandations en tableau, modification par `prompt()` JSON | Cartes QUOI/POURQUOI/IMPACT/ACTION, filtres par domaine, panneau « Voir l'analyse », formulaire « Modifier » qui conserve la proposition d'origine |
| 8 | Assistant enfoui dans un onglet ; « se vendent le mieux », « stock diminue », « paiements en retard » non reconnus | Panneau global NEXORA AI (Ctrl+J) ; 3 intentions ajoutées ; réponses structurées avec graphique, sources cliquables et **recommandations liées** |

## Backend

- **`IGlobalSearchService`** (`GET /api/v1/search?q=&limit=`) — une requête bornée par type d'objet, insensible
  à la casse et aux accents (`nexora.search_norm`). Chaque groupe exige la permission de l'écran qui l'ouvre
  (`catalog.view`, `customers.view`, `suppliers.view`+`purchasing.view`, `hr.view_employees`, `documents.view` —
  limité à ses propres documents sans `documents.view_all` —, `pos.view` + `sales.view`/`reporting.view_sales`).
  Le stock disponible n'apparaît qu'avec `inventory.view` ; aucun champ sensible d'employé. Moins de 2 caractères :
  aucune recherche.
- **`IActivityFeedService`** (`GET /api/v1/dashboard/activity?limit=`) — lit directement ventes, encaissements /
  décaissements, réceptions, clôtures de caisse (écart ⇒ ton « warning »), écritures manuelles, décisions sur
  recommandations ; chaque source conditionnée par sa permission ; tri décroissant. Pas de table d'événements
  parallèle.
- **`RuleBasedAskNexora`** — intentions `top_products` (classement + tendance vs période précédente de même durée),
  `stock_decrease` (décomposition des mouvements de stock réels en valeur, 30 j vs 30 j, part des pertes/casse/
  péremptions, produits en plus forte baisse), mots-clés « en retard » → `overdue_receivables`, dont la réponse est
  désormais **nominative**. Refus explicite sans permission, comme les autres intentions.
- **`AskResponse.Related`** (paramètre optionnel, contrat rétro-compatible) — `SqlAssistantService.QueryAsync`
  joint jusqu'à 3 recommandations ouvertes liées à l'intention, seulement avec `intelligence.view`.
- **`DesignSystemService`** → version 2.0.0.

## Frontend

- `shared/theme.css` : tokens v2 + CSS des composants d'intelligence, du panneau IA et de la palette.
- `shared/intel.tsx`, `ai/` (`nexoraAi.ts` logique pure, `AiConversation`, `NexoraAiPanel`), `app/paletteModel.ts`
  (modèle pur de la palette), `app/experienceApi.ts`, `dashboard/homeModel.ts`, `sales/SaleDetailPage.tsx`
  (`/sales/:id`, cible des ventes trouvées ou listées dans l'activité).
- Liens profonds : `/catalog?q=`, `/suppliers?q=`, `/documents?q=` (filtre par numéro, retirable).

## Défauts trouvés en vérifiant dans le navigateur

1. **Tiroirs non cliquables** : le voile `.nx-overlay` (z-index 60) passait au-dessus du panneau, qui n'avait pas de
   z-index — tout clic était capté par le voile. Défaut présent aussi dans le composant `Drawer` existant (même
   structure) ; corrigé pour les deux (`.nx-drawer, .nx-ai-panel { z-index: 61 }`).
2. Micro-courbe du KPI qui chevauchait la variation ; date d'accueil dans la langue du navigateur au lieu de celle de
   l'application (`dateLong`) ; libellés de preuves manquants (`periodDays`, tranches d'échéance) — corrigés.

## Limites et suite

- Le brief s'arrête à la section 8 ; l'implémentation couvre 1 à 8.
- L'analyse reste **déterministe** (règles SQL + Ask Nexora), sans LLM : NEXORA AI ne comprend que les intentions
  reconnues et le dit sinon (en reproposant des questions). « Pourquoi ? » explique volume / marge / dépenses — la
  « hausse du coût fournisseur » du brief ressort via la règle `supplier_price_increase` jointe en recommandation,
  pas encore comme cause chiffrée de la décomposition du bénéfice.
- RH et Opérations ne produisent pas encore de recommandations (domaines absents des règles Phase 43/54).
- Recherche : pas encore de rapports ni de paramètres côté serveur (couverts côté client par la palette).
- Observé, non modifié : dans l'onglet Fournisseurs, le nom d'un fournisseur pointe vers `/customers/{id}` (écran
  de compte client).
