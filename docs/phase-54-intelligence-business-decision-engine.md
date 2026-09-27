# NEXORA ONE — PHASE 54 — INTELLIGENCE & BUSINESS DECISION ENGINE
## Cahier des charges technique et fonctionnel
### Version 1.0 — Document de référence pour le programmeur

## 1. Objet de la phase
Cette phase transforme NEXORA ONE d'un logiciel de gestion classique en une plateforme proactive capable d'analyser les données de l'entreprise, détecter les problèmes et opportunités, prévoir certaines évolutions, expliquer ses conclusions et proposer des actions concrètes.

Principe fondamental : NEXORA ONE ne décide pas à la place du gestionnaire. Il analyse, explique et recommande.

## 2. Vision fonctionnelle
Le flux principal doit être conçu comme une boucle de décision :

DONNÉES → ANALYSE → DÉTECTION → EXPLICATION → RECOMMANDATION → VALIDATION HUMAINE → ACTION → RÉSULTAT → FEEDBACK

## 3. Architecture fonctionnelle
- Gestion : ventes, stock, finance, POS, comptabilité, clients et fournisseurs.
- Intelligence : analyse, règles métier, prévisions, anomalies et interaction en langage naturel.
- Moteur de décision : transforme les analyses en recommandations priorisées.
- Utilisateur : accepte, modifie ou ignore les recommandations.
- Actions : une recommandation validée peut déclencher une opération métier.

## 4. Nouveau module : Intelligence
Ajouter au menu principal :
- Vue générale
- Recommandations
- Alertes intelligentes
- Analyse des ventes
- Analyse du stock
- Analyse financière
- Analyse des clients
- Analyse fournisseurs
- Prévisions
- Anomalies
- Assistant NEXORA

## 5. Dashboard Intelligence
- État général de l'entreprise.
- Nombre d'alertes critiques, importantes, à surveiller et opportunités.
- Actions prioritaires.
- Opportunités détectées.
- Résumé des tendances ventes, stock et finance.
- Accès direct à l'analyse détaillée de chaque recommandation.

## 6. Système de recommandations
Chaque recommandation doit être persistée en base et explicable.

Exemple : RISQUE DE RUPTURE — Eau minérale 1,5 L
- Stock actuel : 120 unités.
- Ventes moyennes : 32 unités/jour.
- Tendance : +27 %.
- Délai fournisseur : 10 jours.
- Stock de sécurité : 100 unités.
- Quantité recommandée : 250 unités.

Actions : [Voir l'analyse] [Modifier] [Créer bon de commande] [Ignorer]

## 7. Priorités

| Priorité | Signification | Exemples |
|---|---|---|
| CRITIQUE | Action urgente | Rupture imminente, anomalie majeure, problème financier important |
| IMPORTANT | Action à traiter | Stock faible, baisse de marge, baisse importante des ventes |
| À SURVEILLER | Évolution à observer | Tendance inhabituelle, rotation en baisse |
| OPPORTUNITÉ | Possibilité d'amélioration | Forte croissance, produit rentable, potentiel commercial |

## 8. Rules Engine
Créer un moteur de règles déterministes en C#.
- Stock inférieur au seuil de réapprovisionnement.
- Risque de rupture selon consommation et délai fournisseur.
- Hausse ou baisse des ventes au-delà d'un seuil configurable.
- Surstock ou stock dormant.
- Marge en baisse.
- Dépenses inhabituelles.
- Créances élevées ou retards de paiement.
- Évolutions inhabituelles chez les fournisseurs.

Exemple : SI stock_actuel < seuil_reapprovisionnement ET ventes_moyennes > 0 ALORS créer une recommandation de réapprovisionnement.

## 9. Bibliothèque initiale de règles

### 9.1 Stock
- Risque de rupture
- Stock faible
- Surstock
- Stock dormant
- Forte rotation
- Faible rotation
- Hausse de demande
- Baisse de demande
- Stock de sécurité insuffisant
- Quantité de réapprovisionnement recommandée

### 9.2 Ventes
- Hausse inhabituelle
- Baisse inhabituelle
- Produit à forte croissance
- Produit en déclin
- Comparaison période précédente
- Comparaison année précédente
- Produits les plus vendus
- Produits les plus rentables

### 9.3 Finance
- Baisse de marge
- Augmentation des dépenses
- Trésorerie sous pression
- Dépenses inhabituelles
- Augmentation des achats
- Baisse du bénéfice
- Créances élevées

### 9.4 Clients
- Dette importante
- Retard de paiement
- Diminution des achats
- Client très actif
- Client inactif
- Évolution du panier moyen

### 9.5 Fournisseurs
- Augmentation des prix
- Délai de livraison élevé
- Fournisseur principal
- Évolution des achats
- Risque fournisseur à surveiller

## 10. Explicabilité obligatoire
Aucune recommandation importante ne doit apparaître comme une décision opaque.

Structure : PROBLÈME → DONNÉES UTILISÉES → CALCUL → CONCLUSION → ACTION RECOMMANDÉE

Exemple : « Nous recommandons 500 unités parce que les ventes ont augmenté de 16 %, le stock couvre environ 7 jours et le fournisseur nécessite 10 jours pour livrer. »

## 11. Analytics Engine
- Ventes : CA journalier, hebdomadaire et mensuel, croissance, moyennes, médianes, tendances et variations.
- Stock : rotation, stock moyen, jours de couverture, point de commande, stock de sécurité et quantité optimale.
- Finance : marge, marge %, bénéfice, charges, cash-flow, créances et dettes.

## 12. Prévisions
Produire des prévisions à partir de l'historique, des tendances et, lorsque les données sont suffisantes, de la saisonnalité.
- Prévision des ventes.
- Prévision de la demande.
- Prévision du stock.
- Prévisions à 7, 30 jours ou périodes configurables.
- Niveau de confiance et données utilisées.

## 13. Détection d'anomalies
- Ventes inhabituelles.
- Dépenses inhabituelles.
- Mouvements de stock inhabituels.
- Variations importantes de prix ou de marge.
- Comportements clients ou fournisseurs inhabituels.

Une anomalie doit proposer des pistes de vérification sans présenter une hypothèse comme un fait.

## 14. Assistant NEXORA
L'utilisateur doit pouvoir poser des questions en langage naturel.
- Pourquoi mes bénéfices ont diminué ce mois-ci ?
- Quels produits dois-je commander cette semaine ?
- Quel produit risque de tomber en rupture ?
- Pourquoi mes ventes diminuent ?
- Quels clients me doivent le plus ?
- Quels produits sont en croissance ?

L'assistant doit répondre uniquement à partir des données auxquelles l'utilisateur est autorisé à accéder.

## 15. Séparation logique métier / IA
Règle majeure : l'IA ne doit pas être la source de vérité pour les calculs métier.
- C# / Domain = vérité métier.
- PostgreSQL = vérité des données.
- Analytics Engine = calculs analytiques.
- Rules Engine = décisions logiques déterministes.
- IA = compréhension des questions, interaction et explication.

Les calculs de stock, marge, bénéfice, dette, taxes et quantités recommandées doivent être produits par les services métier/analytiques.

## 16. Actions et validation humaine
Flux : Analyse → Recommandation → Validation utilisateur → Action
- Accepter.
- Modifier.
- Ignorer.
- Créer une opération métier après validation.
- Conserver l'historique.

## 17. Apprentissage du contexte de l'entreprise
- Historique des ventes.
- Saisonnalité.
- Produits.
- Clients.
- Fournisseurs.
- Prix.
- Marges.
- Stock.
- Dépenses.
- Comportement et feedback utilisateur.

Exemple : si les ventes augmentent habituellement en décembre, NEXORA ONE peut recommander de préparer les stocks avant cette période.

## 18. Feedback utilisateur
- ID recommandation.
- Utilisateur.
- Action.
- Valeur initiale.
- Valeur modifiée.
- Motif éventuel.
- Date et heure.

## 19. Base de données

| Table | Rôle |
|---|---|
| recommendations | Recommandations générées |
| recommendation_rules | Règles de déclenchement |
| recommendation_events | Événements liés |
| recommendation_actions | Actions exécutées |
| recommendation_feedback | Réponses utilisateur |
| analytics_snapshots | Résultats analytiques historisés |
| forecast_results | Résultats de prévisions |
| anomalies | Anomalies détectées |
| intelligence_settings | Paramètres par entreprise |

Champs minimums de recommendations : id, company_id, type, category, title, description, priority, status, confidence, impact, created_at, expires_at, entity_type, entity_id.

Statuts : NEW, VIEWED, ACCEPTED, MODIFIED, IGNORED, EXPIRED, COMPLETED.

## 20. API à prévoir
```
GET /api/intelligence/dashboard
GET /api/intelligence/recommendations
GET /api/intelligence/recommendations/{id}
POST /api/intelligence/recommendations/{id}/accept
POST /api/intelligence/recommendations/{id}/ignore
POST /api/intelligence/recommendations/{id}/modify
GET /api/intelligence/stock
GET /api/intelligence/sales
GET /api/intelligence/finance
GET /api/intelligence/anomalies
GET /api/intelligence/forecasts
POST /api/intelligence/assistant
```

## 21. Traitements automatiques
- Analyse quotidienne complète.
- Analyse déclenchée après certaines opérations importantes.
- Recalcul des indicateurs.
- Détection d'anomalies.
- Génération et expiration des recommandations.
- Mise à jour des prévisions.

## 22. Architecture technique
React + TypeScript → ASP.NET Core API → Application/Domain → Intelligence → PostgreSQL
- Rules Engine
- Analytics Engine
- Forecast Engine
- Anomaly Engine
- Explanation Engine
- Background Jobs
- Repositories et services d'infrastructure

## 23. Structure des dossiers
```
backend/src/Nexora.Application/Intelligence/DTOs/
backend/src/Nexora.Application/Intelligence/Services/
backend/src/Nexora.Application/Intelligence/Rules/
backend/src/Nexora.Application/Intelligence/Queries/
backend/src/Nexora.Application/Intelligence/Commands/
backend/src/Nexora.Domain/Intelligence/
backend/src/Nexora.Infrastructure/Intelligence/Repositories/
backend/src/Nexora.Infrastructure/Intelligence/Analytics/
backend/src/Nexora.Infrastructure/Intelligence/BackgroundJobs/
frontend/nexora-web/src/modules/intelligence/pages/
frontend/nexora-web/src/modules/intelligence/components/
frontend/nexora-web/src/modules/intelligence/services/
frontend/nexora-web/src/modules/intelligence/hooks/
frontend/nexora-web/src/modules/intelligence/types/
```

## 24. Services C# recommandés
- RecommendationEngine
- RulesEngine
- AnalyticsEngine
- ForecastEngine
- AnomalyEngine
- ExplanationEngine

Méthodes initiales : AnalyzeStock(), AnalyzeSales(), AnalyzeFinance(), AnalyzeCustomers(), AnalyzeSuppliers(), GenerateRecommendations(), CalculatePriority().

## 25. Sécurité et multi-entreprise
- Toutes les données sont isolées par Company/Tenant ID.
- Aucune entreprise ne peut voir les données d'une autre.
- Respect strict des rôles et permissions.
- L'assistant applique les mêmes autorisations que l'application.
- Les opérations sensibles nécessitent une validation humaine.
- Toutes les actions importantes sont auditables.

Permissions : INTELLIGENCE_VIEW, INTELLIGENCE_RECOMMENDATIONS, INTELLIGENCE_FINANCE, INTELLIGENCE_STOCK, INTELLIGENCE_AI, INTELLIGENCE_ACTION.

## 26. Journal d'audit
Enregistrer au minimum : utilisateur, entreprise, date/heure, recommandation, action, ancienne valeur, nouvelle valeur et résultat.

## 27. Design UI NEXORA ONE
- Dark Premium.
- Accent Electric Turquoise #00F5D4.
- Respect du Design System NEXORA ONE.
- Hiérarchie visuelle forte.
- Priorités immédiatement visibles.
- Cartes de recommandations réutilisables.
- Interface responsive desktop/tablette lorsque pertinent.

## 28. Paramètres Intelligence
- Activer/désactiver les recommandations.
- Fréquence d'analyse.
- Seuils.
- Catégories de notifications.
- Permissions.
- Préférences par entreprise.

Exemples : alerter lorsque la couverture est inférieure à 7 jours ; détecter une baisse au-delà de 20 % ; détecter une hausse au-delà de 15 %. Les seuils doivent être configurables.

## 29. Roadmap d'implémentation

| Étape | Module | Livrable |
|---|---|---|
| 54.1 | Infrastructure | Tables, entités, repositories, services et API |
| 54.2 | Rules Engine | Stock faible, rupture, surstock, hausse/baisse ventes |
| 54.3 | Dashboard | Alertes, recommandations, opportunités |
| 54.4 | Analytics | Tendances, croissance, marges, rotations |
| 54.5 | Prévisions | Demande, ventes, stock, saisonnalité |
| 54.6 | Anomalies | Détection des comportements inhabituels |
| 54.7 | Assistant | Questions en langage naturel |
| 54.8 | Actions | Validation et déclenchement d'opérations |
| 54.9 | Feedback | Acceptation, modification, ignorance |
| 54.10 | Intelligence avancée | Prévisions avancées, corrélations, tendances |

## 30. MVP obligatoire
- Stock : stock faible, risque de rupture, surstock, quantité recommandée.
- Ventes : hausse, baisse, produits performants.
- Finance : variation du CA, marge et dépenses.
- Dashboard : alertes, recommandations, opportunités.
- Actions : accepter, modifier, ignorer.
- Explication des raisons et données sources.
- Assistant pour questions simples.

## 31. Critères d'acceptation
1. Une vente modifie le stock et peut déclencher une analyse.
2. Une hausse significative des ventes peut produire une opportunité.
3. Un stock insuffisant peut produire une recommandation.
4. L'utilisateur peut accepter une recommandation.
5. L'utilisateur peut modifier une recommandation.
6. L'utilisateur peut ignorer une recommandation.
7. Les actions importantes sont auditées.
8. Une entreprise ne peut jamais voir les données d'une autre.
9. L'assistant respecte les permissions.
10. Chaque recommandation importante explique son raisonnement.

## 32. Livrables attendus du programmeur
- Schéma PostgreSQL et migrations.
- Entités Domain et services Application.
- Rules Engine documenté.
- Analytics Engine.
- Dashboard React/TypeScript.
- API Intelligence documentée.
- Système de recommandations et statuts.
- Actions accepter/modifier/ignorer.
- Journal d'audit.
- Jobs en arrière-plan.
- Anomalies.
- Prévisions.
- Assistant avec contrôle des permissions.
- Tests unitaires, intégration et API.
- Documentation technique et déploiement.

## 33. Règle fondamentale
« NEXORA ONE doit transformer les données de gestion en informations, les informations en recommandations et les recommandations en actions validées par l'utilisateur. »

## 34. Vision finale
Lorsque le dirigeant ouvre NEXORA ONE, il ne doit pas seulement voir ses chiffres. Il doit comprendre ce qui se passe dans son entreprise, pourquoi cela se produit, ce qui risque de se produire ensuite et quelles actions il peut envisager.

Cette phase constitue donc une évolution stratégique du produit : la fiabilité des données, l'explicabilité, la sécurité multi-entreprise et la validation humaine sont prioritaires.
