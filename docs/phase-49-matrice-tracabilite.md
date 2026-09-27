# Phase 49 — Matrice de traçabilité (exigences → tests)

**Version évaluée :** V001–V062, état du dépôt au 2026-09-22.
**Environnement :** local (PostgreSQL 18, `localhost:5432`), Debug.
**Résultat de la campagne :** **384 tests backend verts** (18 unit + 57 API + 309 intégration) +
**110 tests frontend** (Vitest), 0 échec, 0 test ignoré.

Cette matrice relie chaque famille d'exigences du cahier des charges Phase 49 (§9-47) aux suites de
tests **qui existent réellement** dans le dépôt. Elle ne crée pas de test fictif : lorsqu'une
famille n'est couverte que partiellement, ou pas du tout par du code (processus humain, matériel,
outillage externe), c'est écrit.

### Légende

| Statut | Signification |
|---|---|
| **COUVERT** | Au moins un test automatisé vérifie le comportement attendu, bout-en-bout ou au niveau service + base |
| **PARTIEL** | Une partie des points de la section est testée ; les manques sont listés |
| **MANUEL** | Relève d'une attestation humaine (UAT, audit, matériel) — suivi par un point `manual` de la checklist Go-Live |
| **HORS CODE** | Relève de l'infrastructure, de la CI ou d'un outillage externe — jamais vérifiable par l'application elle-même |

Emplacement des suites : `backend/tests/Nexora.IntegrationTests/` (base PostgreSQL réelle clonée),
`backend/tests/Nexora.ApiTests/` (HTTP réel via `WebApplicationFactory`, rôle `nexora_app`),
`backend/tests/Nexora.UnitTests/`, `frontend/nexora-web/src/**/*.test.ts`.

---

## Niveaux de test (§9-12)

| § | Famille | Tests | Statut | Manques |
|---|---|---|---|---|
| 9 | Unitaires (règles, calculs, validation, formats) | `Nexora.UnitTests` (18) ; `Totp` RFC 6238, `TemplateRenderer`, `TabularParser` | COUVERT | — |
| 10 | Intégration (API, DB, jobs, queues, documents, paiements, webhooks, connecteurs, POS, mobile, sync) | 39 classes d'intégration (309 tests) — `FinancialEngineTests`, `PosSalesTests`, `NotificationEngineTests`, `IntegrationsEcosystemTests`, `OfflineSyncTests`, `MobileFieldOpsTests`, `SchedulerTests`… | COUVERT | Email/SMS réels : canaux simulés (`LoggingPushChannel`, stubs) |
| 11 | Fonctionnels (créer, consulter, modifier, valider, annuler, rembourser, archiver, exporter, rechercher, approuver, clôturer) | Répartis dans les classes métier ; retour `PosSalesTests.Partial_return_…`, clôture `GeneralAccountingTests`, approbation `SecurityAuditTests.Refund_above_threshold_…` | COUVERT | — |
| 12 | End-to-end (utilisateur → interface → service → base → intégration → document → audit → rapport) | **`SmokeTests`** (NEW, HTTP réel) + `PosSalesTests.Full_supply_chain_scenario_reconciles` | PARTIEL | Aucun test navigateur (Playwright/Cypress) : l'étape « interface » est couverte par les tests de contrat client Vitest, pas par un pilotage réel du DOM |

## Métier (§13-27)

| § | Famille | Tests | Statut | Manques |
|---|---|---|---|---|
| 13 | Ventes | `PosSalesTests` (10), `SalesOrderTests` (8), `CatalogPricingTests` (9), `CommercialDocumentsTests` (7) | COUVERT | — |
| 14 | Achats | `PurchaseThreeWayMatchTests` (7), `CatalogInventoryTests` (11) | COUVERT | Retour fournisseur : type de mouvement `purchase_return` existe, pas de test dédié |
| 15 | Stock | `StockLifecycleTests` (9), `CatalogInventoryTests` | COUVERT | — |
| 16 | Finance | `TreasuryCockpitTests` (8), `FinancialEngineTests` (10), `IntegrationsEcosystemTests` (rapprochement bancaire) | COUVERT | — |
| 17 | Comptabilité | `GeneralAccountingTests` (9), `FinancialEngineTests`, `GroupConsolidationTests` (8) | COUVERT | — |
| 18 | POS | `PosSalesTests`, `OfflineSyncTests`, `offlineQueue.test.ts` (7) | PARTIEL | Scanner/imprimante/tiroir : périphériques physiques — MANUEL |
| 19 | Restaurant | `SectorModulesTests.Restaurant_order_uses_core_checkout_and_consumes_recipe_ingredients` | PARTIEL | Tables, modificateurs, écran cuisine, split, transfert : **non testés** |
| 20 | Pharmacie | `StockLifecycleTests` (lots, FEFO, expiration, rappel, blocage), `SmokeTests` (vente d'un produit suivi par lot) | COUVERT | — |
| 21 | Commerce / supermarché | `CatalogPricingTests` (PLU, promotions, recherche tolérante, scan) | PARTIEL | Rayons : pas de test dédié |
| 22 | Services | `SectorModulesTests.Service_product_sells_without_stock_movement…`, `CommercialDocumentsTests` (devis → commande), `MobileFieldOpsTests` (interventions, livraisons) | COUVERT | — |
| 23 | Multi-entreprises | `GroupConsolidationTests` (8), `IdentityAccessTests` (6), **`MultiTenantIsolationTests`** (NEW) | COUVERT | — |
| 24 | Crédits | `PosSalesTests.Credit_*`, `CustomerCollectionsTests` (7), `PurchaseThreeWayMatchTests` (dettes) | COUVERT | — |
| 25 | Reporting | `DashboardReportingTests` (9), `ReportingTrustTests` (6), `BiReportingTests` (7) | COUVERT | — |
| 26 | Intelligence | `IntelligenceEngineTests` (12) | COUVERT | — |
| 27 | GED | `GedDocumentsTests` (6), `GedEndpointsTests` | PARTIEL | OCR : heuristique texte uniquement (assumé Phase 45) ; « restore » d'un document : non testé |

## Plateforme, sécurité, exploitation (§28-47)

| § | Famille | Tests | Statut | Manques |
|---|---|---|---|---|
| 28 | API | `ApiKeyAuthenticationTests`, `IdempotencyFilterTests`, `ApiKeyPlatformTests` (scopes, quota → 429), `MarketplaceEcosystemTests` (pagination par curseur) | COUVERT | Politique de dépréciation de version : non outillée |
| 29 | Mobile | `MobileFieldOpsTests` (6), `MobileEndpointsTests`, `OfflineSyncTests.Device_register_list_and_revoke` | PARTIEL | Caméra, GPS réel, push réel, appareils physiques : MANUEL |
| 30 | Offline | `OfflineSyncTests` (8) — double envoi, conflit stock, crédit hors ligne, échec jamais confirmé ; `offlineQueue.test.ts` | COUVERT | Réseau faible/coupure brutale réels : MANUEL |
| 31 | Sécurité | `SecurityAuditTests` (7), `MfaAuthenticationTests` (7), `SecurityGovernanceTests` (8), `IdentityAccessTests` | PARTIEL | XSS / CSRF / injection / SSRF : **pas de sonde adversariale automatisée** (SAST/DAST — HORS CODE). SSRF sur URL de webhook sortant : écart déjà documenté Phase 46 |
| 32 | Multi-tenant | **`MultiTenantIsolationTests`** (NEW, 4) + point de contrôle **`multi_tenant_isolation`** (NEW, réel) + `DataModelIntegrityTests` (RLS) + `SecurityAuditTests.Audit_query_is_scoped…` | PARTIEL | Couvert : lecture par id, liste, recherche, registre d'incidents. Non couvert par un test dédié : exports, notifications, cache, logs |
| 33-34 | Performance / charge | `PosSalesTests.Two_registers_selling_same_stock_never_oversell` (concurrence) | HORS CODE | Aucun banc de charge (k6/JMeter) ; point `performance_targets_met` = MANUEL |
| 35 | Résilience | `ContinuityRegisterTests` (12) — verrouillage d'urgence, exercices DR, page de statut | PARTIEL | Panne réelle serveur/DB/réseau, failover : HORS CODE |
| 36 | Backup / restore | `ContinuityRegisterTests`, `OperationsReadinessTests.Backup_and_restore_controls_turn_green…` | PARTIEL | Registre applicatif testé ; restauration physique `pg_restore` et PITR : HORS CODE |
| 37 | UX | — | MANUEL | Tests d'usage avec utilisateurs, temps de tâche |
| 38 | Accessibilité | `UxPreferencesTests` (contraste élevé, échelle de texte persistés) | MANUEL | Audit WCAG 2.2 AA (clavier, lecteur d'écran) : jamais fait |
| 39 | Compatibilité | — | MANUEL | Matrice navigateurs / OS / périphériques |
| 40 | Localisation | `UxPreferencesTests` (langue, fuseau horaire) | PARTIEL | Traductions EN / créole : non vérifiées ; formats de documents localisés : non testés |
| 41 | Intégrations | `IntegrationsEcosystemTests` (11) — adaptateur de paiement simulé, webhooks signés, relevés bancaires | PARTIEL | Aucun prestataire réel (PSP, Mobile Money, SMS, email) : sandbox à brancher |
| 42 | Administration | `OperationsMaintenanceTests` (9), `AdministrationTests` (9), `NotificationEngineTests` (dead-letter) | COUVERT | — |
| 43 | Données | `DataModelIntegrityTests` (7), `ReportingTrustTests` (qualité des données, réconciliation KPI), `SecurityAuditTests` (audit immuable) | COUVERT | — |
| 44 | Migration | `MigrationImportTests` (11) — simulation sans écriture, rollback, réconciliation | PARTIEL | Importeurs livrés : clients, fournisseurs, produits, stock d'ouverture. Factures, dettes, documents, comptabilité : **pas d'importeur** |
| 45 | Installation | — | HORS CODE | Aucun installeur ; `rebuild.sh` / `apply.sh` + `README` uniquement |
| 46 | Mise à jour | `OperationsReadinessTests.A_deployment_is_logged_then_reversible` | PARTIEL | Registre des déploiements réversible testé ; migration descendante (down) : inexistante |
| 47 | Monitoring | `OperationsEndpointsTests` (health, ready, metrics), `OperationsReadinessTests.System_status_…`, `ResilienceEndpointsTests` (page de statut) | COUVERT | — |

## Parcours de fumée (§52) — `SmokeTests`

| Étape §52 | Réalisation | Résultat |
|---|---|---|
| Login | `POST /auth/login` | ✅ |
| Dashboard | `GET /dashboard/command-center` | ✅ |
| Créer client | **aucune API de création de client** — client démo existant réutilisé, vérifié via `GET /credit/statements/{id}` | ⚠️ constat A1 |
| Vente | `POST /sales` (produit suivi par lot, FEFO) | ✅ |
| Paiement test | même transaction atomique (Phase 6) — `amountPaid = total` | ✅ |
| Stock | `GET /stock/movements` — mouvement de sortie journalisé | ✅ |
| Facture | numéro de document définitif émis avec la vente | ✅ |
| Recherche | `GET /catalog/search?q=Paracetamol` | ✅ |
| Logout | jeton retiré → `401` | ⚠️ constat A2 |
| Health | `GET /ready` | ✅ |

---

## Anomalies relevées par la campagne

Criticité selon l'échelle §5. Aucune n'est un P0 ou un P1.

| ID | Titre | Criticité | Constat | Statut |
|---|---|---|---|---|
| **A1** | Aucune API de création de client ni de fournisseur | **P2** | `SuppliersController` est en lecture seule, il n'existe pas de `CustomersController`. Les tiers ne peuvent être créés que par le pipeline d'import (Phase 39) ou en SQL. Un commerçant ne peut donc pas créer un nouveau client depuis la caisse ou l'écran de vente. Contournement : import CSV d'une ligne | Ouverte — à arbitrer |
| **A2** | La déconnexion ne révoque pas la session côté serveur | **P3** | `AuthContext.logout()` vide seulement le stockage local. La route `POST /security/sessions/{id}/revoke` existe mais n'est pas appelée : la session reste « active » dans l'écran Sessions et le jeton reste valable jusqu'à expiration s'il a été copié | Ouverte — à arbitrer |
| **A3** | Les tests API écrivent dans la base `nexora`, qui sert de modèle aux tests d'intégration | **P2** (outillage) | `FinanceEngineFixture` clone `nexora` par `CREATE DATABASE … TEMPLATE nexora`. Tout ce qu'un test API y laisse est hérité par la campagne suivante. **Survenu pendant cette phase** : un réglage `stock.negative_stock_allowed=true` laissé par une première version de `SmokeTests` a fait échouer 4 tests de stock (`StockLifecycleTests`, `PosSalesTests`, `OfflineSyncTests` ×2). Réglage supprimé, stock réaligné, `SmokeTests` réécrit pour n'amorcer le stock que par un mouvement `opening` cohérent. 309/309 verts depuis | Corrigée pour ce cas — risque structurel ouvert |
| **A4** | Aucune base de démo n'a de stock | **P4** | Aucune des 3 entreprises démo n'a de mouvement d'entrée : une démonstration « vendre un produit » échoue sans préparation | Ouverte |

## Couverture en chiffres

| Mesure | Valeur |
|---|---|
| Familles §9-47 (§33-34 regroupées) | 38 |
| COUVERT | 19 |
| PARTIEL | 14 |
| MANUEL | 3 (UX, accessibilité, compatibilité) — plus les parts matérielles de POS / mobile / offline |
| HORS CODE | 2 (performance/charge, installation) — plus les parts infrastructure de résilience / backup / sécurité |
| Anomalies P0 / P1 ouvertes | 0 / 0 |
| Anomalies P2 ouvertes | 2 (A1, A3 structurel) |
