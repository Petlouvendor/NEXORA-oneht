# 10 — Plan de correction (édition 2)

> Proposition seulement : aucune correction n'a été faite pendant l'audit. Ordre du cahier d'audit : (1) sécurité et
> intégrité des données, (2) compilation et exécution, (3) fonctionnalités essentielles inutilisables, (4) incohérences
> frontend / backend / base, (5) flux métier, (6) fonctionnalités partielles, (7) tests, (8) performance et
> maintenabilité, (9) secondaire. Une échéance fixe (A-042) passe devant. Chaque correction commence par le test qui
> reproduit le défaut (rouge avant, vert après). Le plan de l'éd. 1 a été exécuté (étapes 0 à 20, voir
> `docs/corrections-audit-2026-10.md`) ; il reste dans l'historique Git.

## Étape 0 — Préalables

| ID | Action | Critère d'acceptation |
| --- | --- | --- |
| T-003 | `CommandTimeout` explicite sur le clonage de la fixture (`FinanceEngineFixture.cs:48-58`) | `dotnet test` d'intégration vert sans variable d'environnement |
| A-036 | Épingler `System.Security.Cryptography.Pkcs` corrigé dans `Directory.Packages.props` | Scan de vulnérabilités vide, CI verte |
| Tests multidevise | Ajouter un jeu de test à taux non entier (USD à 132,5) | Les tests de A-041 et A-052 échouent avant correction |

## Priorité 0 — Échéance du 2027-01-01

| ID | Fichiers | Cause | Correction | Risques | Tests | Critère |
| --- | --- | --- | --- | --- | --- | --- |
| **A-042** | `FinanceServices.cs` (`ResolveOpenPeriodAsync`), `FinancialEngine.cs:61-69`, `V072…sql:94-98`, `SqlGeneralAccounting.cs:613-616`, écran comptabilité | Exercices et périodes créés seulement à l'amorçage | Route + écran « ouvrir l'exercice suivant » (12 périodes) ; création automatique planifiée en décembre ; message métier clair si aucune période | Faible ; la clôture de l'exercice précédent reste indépendante | Écriture datée du premier jour d'un exercice absent → refus explicite ; après création → acceptée ; vente du 2027-01-01 en test d'horloge | **Avant le 2026-12-31** sur chaque installation : exercice 2027 créé et vente du 1er janvier possible |

## Priorité 1 — Intégrité des données et sécurité

| ID | Fichiers | Correction | Dépendances / risques | Tests de validation | Critère |
| --- | --- | --- | --- | --- | --- |
| **A-052** | `SalesService.cs:418`, `SalesReturnService.cs:200`, `PostingRuleSet.cs:94-100` | Coût des ventes passé en devise fonctionnelle (`new Money(cogsTotal, functional)`) | Données existantes : script de correction (écritures correctives datées, pas de réécriture) pour les ventes et retours en devise déjà comptabilisés — 2 ventes dans `nexora_recette` | Vente et retour en USD : 6031/3110 = coût en HTG | Compte de stock = valorisation (à l'écart de prix près) |
| **A-043** | `SqlFinancialReports.cs:35,66,77,171`, `SqlGeneralAccounting.cs:665,741`, 12 fichiers / 23 requêtes `status = 'posted'` | Agréger `posted` **et** `reversed` (ou ne plus changer le statut de l'origine) ; un seul prédicat partagé | Clôtures déjà faites après une extourne : vérifier puis corriger par écriture | Écriture, extourne, puis balance / bilan / résultat / clôture / à-nouveaux | Effet net nul après extourne dans tous les états |
| **A-049** | `BillingController.cs:62-69`, `SqlPlatformBilling.cs:167-232`, `ProductionConfiguration.cs:17-50`, `docker-compose.prod.yml` | Démarrage refusé hors développement sans jeton fort ; signature HMAC ; contrôle montant / client / devise avant de solder ; limite de débit ; ou route désactivée hors SaaS | Aucune pour une installation mono-client | Démarrage Production sans jeton → échec ; jeton par défaut → 401 ; montant partiel → facture non soldée | Plus de facture soldée par un appel anonyme |
| A-040 | `SalesInfrastructure.cs:88-91`, `SalesContracts.cs:3-4`, `SqlSalePricingGuard` | Taxe = taxe du produit ; dérogation seulement avec `pos.change_price`, journalisée | Hors ligne : même règle dans le profil | Vente avec `taxId` 0 % → 143 ou 403 | Taxe client ignorée |
| A-038 | `InventoryOperationsService.cs:159-200,260-283,365-456`, `CatalogInventoryStores.cs:462-534` | `SELECT … FOR UPDATE` du document et `UPDATE … WHERE status = …` conditionnel (réception, annulation, validation) | Faible | 8 réceptions simultanées → 1 réussie, 7 en 409 | Quantité reçue = quantité expédiée |
| A-003 résidu | `SyncServices`, `SyncController` (`/sync/devices`, `/sync/batch`) | Enrôlement d'appareil approuvé par un responsable ; vente hors ligne refusée (ou mise en conflit) si la session est close ; date bornée à [ouverture, clôture] | Caisses hors ligne existantes : réenrôlement | `/sync` dans une session close → conflit | Aucune vente ajoutée à une caisse comptée |
| A-041 | `PostingRuleSet.cs:57-117,168-220,328-399,572-615`, `JournalEntryDraft.cs:73-80`, `Money.cs:32` | Convertir le total puis dériver la dernière composante par différence (comme les achats et la paie) ; message métier au lieu du message PostgreSQL | Toutes les règles de comptabilisation en devise | Vente, retour, facture en USD à 132,5 ; OD 10,01 + 10,01 | Aucun refus d'équilibre sur une opération valide |
| A-045 | `FinanceController.cs:19-72`, `FinanceOperations.cs:87-112`, `V022…sql:60-67` | Retirer `/finance/*` ou réserver à un rôle technique ; sinon mêmes contrôles que les services métier (4 yeux, session, plafonds) | Aucun écran ne les appelle | Comptable sur `/finance/expenses` → 403 ou approbation requise | Dépense jamais payée sans second approbateur |
| A-048 | `IntelligenceController.cs:34-110`, `V056…sql:230-231` | Filtrer par catégorie selon les permissions de pilotage, ou exiger `reporting.view_financial` | Faible | Responsable stock : `/intelligence/forecasts` → 403 | Même réponse que `/forecasts` |
| A-033 | `UserAssignmentService.cs:26-42,55-68,137-186,219-223` | `admin.manage_users` exigée dans l'entreprise visée | Faible | Admin A / caissier B : action sur B → 403 | — |
| A-034 | `PermissionAuthorization.cs:51-56`, `AuditAndFraud.cs`, audit RH, clonage | Borner à l'entreprise active ou vérifier la permission par entreprise lue | Motif transverse : recenser (voir 09 §2) | Lecture d'audit multi-entreprise → entreprise active seule | — |
| A-035 | `SecurityGovernance.cs:316-333` | Statut changé seulement si le retrait réussit | Nul | Retrait refusé → erreur, statut inchangé | — |
| A-009 résidu | migration | RLS sur `app_user`, `user_role`, `access_scope` (lecture de soi, portée système pour l'authentification) | Élevé : chemins d'authentification ; suite complète avec `nexora_app` | Utilisateur d'une autre entreprise invisible en SQL | 0 table d'identité sans RLS |

## Priorité 3 à 6 — Fonctionnement et rapports

| ID | Correction | Critère |
| --- | --- | --- |
| A-046 | Convertir en devise fonctionnelle au taux du jour (ou afficher par devise) dans KPI, trésorerie, pilotage | Trésorerie de la Pharmacie = HTG + USD × taux |
| A-047 | Retenir le budget actif seulement ; signe selon le type de compte | Révision ≠ doublement |
| A-022 résidu | `SqlAgingBudgetDrill.cs:41` : `remaining_amount` (avoirs déduits) | Âgé du pilotage = âgé des achats |
| A-039 | Simulateur produit par `ISalesPricing.PriceCartAsync` | Simulateur = caisse |
| A-044 | Réouverture = extourne de l'écriture de clôture + annulation de la clôture | Réouvrir puis reclôturer : résultat non doublé |
| A-050 | Écriture d'ouverture (stock / report à nouveau) dans la transaction d'import | Stock importé = compte de stock |
| A-037 | Réappliquer l'instantané, ou retirer le « retour arrière » | Retour arrière visible dans les paramètres |
| A-051 | Statuts « déclarée » / « contrôlée » distincts ; « vérifiée » seulement après `restore.sh --verify` | Aucune sauvegarde « vérifiée » sans restauration |
| FAIBLES | 90 constats par fichier (09 §5) : confirmation forte du versement d'avance, `/sync` 500 sur date non UTC, variables HTML non échappées dans les e-mails, pièce jointe servie en ligne, prévision datée de 2300, etc. | Au fil des modules |

## Priorité 7 — Tests à ajouter

Ceux de `08-tests-et-resultats.md` §4 ; suite E2E sur installation neuve ; fournisseurs en bac à sable (MonCash, SMTP,
Twilio, FCM) ; test de charge ; test d'intrusion externe ; autorisations par rôle sur les routes sans écran.

## Ordre d'exécution recommandé

1. Étape 0 (T-003, A-036, jeu de test multidevise).
2. **A-042** — à livrer et déployer avant le 2026-12-31.
3. A-052 puis A-043, avec scripts de correction des données existantes.
4. A-040, A-038, A-003 résidu (fraude et stock).
5. A-041 (multidevise), puis A-046.
6. A-049 (avant toute exposition de la plateforme SaaS).
7. A-045, A-048, A-033, A-034, A-035, A-009 résidu.
8. Le reste par priorité, puis recette complète (unitaires, intégration avec `nexora_app`, API, Vitest, E2E).
