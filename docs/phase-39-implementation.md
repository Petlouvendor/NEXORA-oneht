# Phase 39 — Migration, Importation & Onboarding avancé — implémentation

**Statut : livré et vérifié.** Migration `V052` (6 tables + 2 permissions).
11 tests d'intégration (`MigrationImportTests`), 2 tests API (`MigrationEndpointsTests`),
3 tests frontend (`migrationApi.test.ts`).
**281 tests backend** verts (18 unit + 21 API + 242 intégration), Debug **et** Release, 0 warning.
63 tests frontend. `tsc` + `vite build` verts.

Une entreprise peut démarrer avec NEXORA ONE **sans repartir de zéro** : le **pipeline d'import
générique** profile, mappe, valide, **simule sans impact**, puis intègre des données CSV / JSON,
avec erreurs localisées ligne par ligne, rapprochement post-import et **rollback avant
finalisation**. Une **checklist d'onboarding** dérivée en direct + un **score de migration**
guident la mise en service.

### Socle réutilisé

- **Phase 5** — `customer` / `supplier` / `product` / `product_category` / `unit` / `product_price` (cibles d'import)
- **Phase 5** — `IStockLedger` (écrivain unique des stocks) + type de mouvement `opening` (V024) pour le stock d'ouverture (§14)
- **Phase 11** — `audit_log` immuable (trigger) → audit migration (§36)
- **Phase 16** — `SchedulerRunner` → reprise des imports restés en cours (§27, §40)
- **Phase 31** — `IDataQualityService` (contrôles qualité — le score s'en inspire, §34)

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-2 | `SOURCE → IMPORT → PROFILAGE → MAPPING → VALIDATION → PRÉVISUALISATION → IMPORT TEST → VÉRIFICATION → IMPORT FINAL → RAPPROCHEMENT` ; jamais d'import direct sans validation ; conserver le fichier ; chaque import a un identifiant + un auteur + un historique ; erreurs **ligne par ligne** | **`IImportService`** : `created → profiled → mapped → validated → simulated → executing → completed`. `import_job.content` conserve le fichier original + `content_hash` SHA-256 (§22, §42). `import_job.created_by` + `import_event` (ledger §46) + `audit_log`. **`import_row`** porte le statut, la correspondance et les messages **par ligne** (`row_number` 1-based) |
| 3, 44 | Sources : Excel, CSV, JSON, XML, ancienne DB, API, autre ERP/POS, fichiers bancaires, exports comptables, manuel ; formats standards CSV UTF-8 / XLSX / JSON | **`TabularParser`** — CSV RFC 4180 (guillemets, séparateurs échappés, retours à la ligne dans les cellules, délimiteur auto-détecté `, ; \t |`, BOM UTF-8 retiré, en-têtes dé-doublonnés) + JSON (tableau d'objets plats, ou `{data:[…]}`). Aucune dépendance externe. **XLSX → « exportez en CSV »** (parseur binaire = backlog) ; ancienne DB / API / formats bancaires : via les connecteurs Phase 12 (backlog d'import) |
| 4 | Données importables (entreprises, établissements, utilisateurs, clients, fournisseurs, produits, catégories, prix, stocks, lots, commandes, factures historiques, paiements, créances, dettes, comptes, soldes d'ouverture, immobilisations, budgets, documents, configuration) | **4 importeurs livrés** : `customers` (§12), `suppliers` (§13), `products` (§11, + catégorie & unité créées à la volée, + `product_price` vente), `opening_stock` (§14 → mouvement `opening` via `IStockLedger`). Le pipeline est **générique** (`IEntityImporter`) — les autres types (ventes/achats/créances/dettes/comptabilité §15-21, documents §22) branchent le même contrat : **backlog** |
| 5 | Assistant : `1 SOURCE → 2 FICHIER → 3 PROFILAGE → 4 MAPPING → 5 VALIDATION → 6 SIMULATION → 7 IMPORT → 8 CONTRÔLE → 9 FINALISATION` | `POST /imports` → `/profile` → `/mapping` → `/validate` → `/simulate` → `/execute` → `/reconciliation`. Écran **`MigrationWizardPage`** (bandeau d'étapes) |
| 6 | Profilage : nombre de lignes, colonnes, types détectés, valeurs manquantes, doublons, formats invalides, valeurs uniques, dates, devise, montants, références | **`ImportProfiler`** → `ImportProfileDto` : `rowCount`, `columns`, par colonne `{detectedType [text/integer/decimal/date/boolean/email/phone/empty], nonEmpty, empty, distinctValues, samples[5], invalidFormat}`, `duplicateRows` (lignes strictement identiques), `detectedCurrency` (colonne « devise/currency »). Détection de type **stricte** (la valeur brute doit *ressembler* au type, pas seulement être coercible) |
| 7, 33 | Mapping : colonne source → champ NEXORA, **automatique suggéré**, manuel, **sauvegardé**, transformations, valeur par défaut ; assistant intelligent (suggestion mapping / catégories / devise) | **`SuggestMappingAsync`** : dictionnaire de synonymes FR/EN par champ (`code`↔`reference/ref/id/matricule`, `name`↔`nom/raisonsociale/designation`, `phone`↔`tel/mobile/gsm`, `sale_price`↔`prix/pvttc/tarif`, `location`↔`emplacement/entrepot/depot`…), matching normalisé (lettres+chiffres). **`import_mapping_template`** (`SaveAsTemplate`) réutilisable. `ColumnMappingDto {targetField, sourceColumn?, defaultValue?, transform?}` |
| 8 | Transformation : trim, téléphone, email, dates, décimales, séparateurs, unités, codes, catégories, taxes, statuts, devise | **`TabularParser.ApplyTransform`** : `trim` (vide → null), `lower`/`upper`, `email` (trim+lower), `phone` (chiffres + `+` de tête), `date` (10 formats → `yyyy-MM-dd`), `decimal`/`number` (détecte le séparateur décimal `,` vs `.`, retire les séparateurs de milliers), `integer`, `boolean` (`1/oui/yes/vrai/x` → true) |
| 9 | Correspondance entités : `SOURCE → MATCH {EXACT / PROBABLE / NO MATCH} → VALIDATION` (identifiant externe, e-mail, téléphone, nom, code, référence) | `import_row.match_type` ∈ `exact` (code/SKU) / `probable` (e-mail ou nom identique) / `none`. `RowProcessResult.MatchType` remonté par chaque importeur |
| 10, 28 | Doublons : détection, score de similarité, règle métier, fusion, journal ; **idempotence** (identifiant import + identifiant source, empêcher le doublon, modes création/MàJ/upsert/rejet) | Doublons **dans le fichier** : `dedup_key` (code/SKU/`sku\|location`) — un `HashSet` marque la 2ᵉ occurrence `warning` à la validation puis `skipped`/`rejected` à l'exécution. Doublons **vs base** : `MatchAsync` par clé exacte. Fusion assistée / score flou : **backlog** |
| 11-14 | Import produits (SKU, code-barres, nom, catégorie, prix, coût, unité, taxe, type) ; clients (code, nom, tél, e-mail, adresse, crédit) ; fournisseurs (code, nom, contacts, crédit) ; **stocks** (entrepôt, emplacement, produit, quantité, coût — état initial documenté avec écriture / événement d'ouverture) | `CustomerImporter` / `SupplierImporter` : upsert `customer` / `supplier` par `customer_number` / `supplier_number` + devise par défaut de l'entreprise. `ProductImporter` : upsert `product` par `sku`, **catégorie créée si absente** (tracée dans `import_record`), **unité créée si absente** (`UN` par défaut), `product_price` vente si `sale_price`. `OpeningStockImporter` : produit + emplacement résolus (code **ou nom** d'`inventory_location` / d'`warehouse`), `IStockLedger.PostAsync(MovementType.Opening, +qty, cost, reference_type='import', reference_id=job)` — **idempotent** (un 2ᵉ import est `skipped` si un mouvement `opening` existe déjà) |
| 15-21 | Historique ventes / achats ; créances / dettes ; comptabilité (plan comptable, journaux, soldes d'ouverture, `ANCIEN PLAN → TABLE DE CORRESPONDANCE → PLAN NEXORA ONE → BALANCE OUVERTURE → CONTRÔLE DÉBIT = CRÉDIT`) ; migration financière | **Backlog** — le pipeline (`IEntityImporter` + mapping + simulation + rollback) est prêt à les accueillir. La table de correspondance comptable est un cas particulier de `import_mapping_template` ; le contrôle débit = crédit s'appuiera sur `IAccountingControls` (Phase 29) |
| 22 | Documents (factures, reçus, contrats, pièces, images, PDF, métadonnées, hash) | **Backlog** — `issued_document` (Phase 30) porte déjà snapshot + hash + code de vérification |
| 23 | **Import test** : sandbox, échantillon, résultats, erreurs, warnings, temps estimé, impact estimé, **aucune modification production** | **`SimulateAsync`** : chaque ligne passe par `importer.ProcessAsync(DryRun: true)` — **les importeurs ne font que lire en DryRun** (`if (!ctx.DryRun)` garde chaque écriture). Aucune transaction, aucune écriture. `SimulationDto {rowsOk, warnings, errors, duplicates, wouldCreate, wouldUpdate, wouldReject, sample[25]}` figé |
| 24 | Prévisualisation : `N lignes OK / N warnings / N erreurs / N doublons / N créations / N MàJ / N rejets → VALIDER / ANNULER` | `SimulationDto` (voir §23) affiché dans l'écran avant le bouton « Lancer l'import final » |
| 25 | Validation : champs obligatoires, références, types, dates, montants, devises, taxes, unités, permissions, contraintes métier, intégrité | **`ValidateAsync`** : `importer.ValidateAsync` par ligne (sans écriture) → `import_row.status` ∈ valid/warning/error + `messages` `[{level, field, message}]`. Compteurs `ok_rows` / `warning_rows` / `error_rows` / `duplicate_rows`. Permissions : `[RequirePermission(migration.manage)]` |
| 26 | Gestion erreurs : bloquante / warning, ligne rejetée, **correction inline**, export erreurs, réimport des lignes corrigées, historique | `GET /imports/{id}/errors` (lignes `error,rejected`) ; **`POST /imports/{id}/rows`** (`CorrectRowAsync`) : réécrit `import_row.source` + revalide. Export CSV des erreurs : le front peut le composer depuis `/errors` (bouton = backlog) |
| 27 | Import par lots : batch, taille configurable, progression, **pause / reprise**, retry, journal, contrôle mémoire | `import_job.batch_size` (1–10000, défaut 500) ; `RunAsync` traite par lots **une transaction par lot** ; `PauseAsync` (`paused_at`) vérifié entre les lots → `resume` reprend depuis `processed_rows` ; `ResumeStalledAsync` (planificateur, jobs `executing` figés > 10 min) |
| 28-29 | Idempotence + **modes** : créer uniquement / MàJ uniquement / créer ou MàJ / ignorer doublons / rejeter doublons / synchro continue (connecteurs) | `import_job.mode` (`create_only` / `update_only` / `upsert` / `skip_duplicates` / `reject_duplicates`). `Decide(mode, exists)` : `create_only`+exists → skipped, `update_only`+!exists → skipped, `skip_duplicates`+exists → skipped, `reject_duplicates`+exists → rejected, sinon upsert. Synchro continue : les connecteurs Phase 12 (backlog) |
| 30 | Import initial **multi-entreprises** : mapping par entreprise, catégories/plans/devises/entrepôts/rôles différents, validation de scope | Toutes les tables `import_*` sont **RLS entreprise** (`enable_company_rls`) ; le service lit `tenant.ActiveCompanyId` ; un mapping est un `import_mapping_template` **par entreprise**. Chaque import est borné à une entreprise |
| 31-34 | **Onboarding automatique** (`TYPE ENTREPRISE → PROFIL SECTEUR → CONFIG → IMPORT → UTILISATEURS → DONNÉES → CHECKLIST → ACTIVATION`) ; **checklist** 19 items ; **assistant intelligent** ; **score** (données / mapping / qualité / comptabilité + PRÊT) | **`IOnboardingService.GetChecklistAsync`** : 17 items, la plupart **dérivés en direct** (`taxes`, `establishments`, `warehouses`, `cash_register`, `users` via `user_role`+`access_scope`, `product`, `customer`, `supplier`, mouvement `opening`, `account`, `financial_account`, `document_sequence`, `backup_policy`) ; `logo` via `company_branding.logo_url` ; `notifications`/`tests` **manuels** (`onboarding_task`). **`MigrationScoreDto`** : `data` = % des items « données » faits, `mapping` = 100 si import complété / 60 si mappé / 0, `quality` = `100 − (lignes en erreur / lignes totales)` sur tous les imports, `accounting` = % des items « accounting », `ready` = tous les items auto faits **et** `data ≥ 60` |
| 35 | **Rapprochement post-import** : lignes source vs NEXORA ONE, total stock, créances, dettes, balance, CA, paiements, documents, écarts, validation | **`ReconcileAsync`** → `ReconciliationDto {lines[{metric, sourceValue, nexoraValue, delta, ok}], allReconciled}` : « Lignes source vs intégrées », et selon le type : nombre de clients / fournisseurs / produits enregistrés (`import_record`), ou **quantité d'ouverture importée** (somme des mouvements `opening` de ce job). Figé dans `import_job.reconciliation` + événement `MigrationReconciled` |
| 36 | Audit migration : source, fichier, utilisateur, date, mapping, version, nb lignes, erreurs, créations, MàJ, rejets, validation, finalisation | `import_job` (tous ces champs) + `trg_audit_import_job` → `audit_log` immuable + `import_event` (13 types §46) |
| 37 | **Rollback** : annulation avant finalisation, suppression des objets créés lorsque sûr, transaction par lot, procédure corrective pour les données déjà liées, journal complet | **`CancelAsync`** : pour chaque `import_record` `action='created'` non révoqué → `SET LOCAL nexora.allow_hard_delete='on'` + `DELETE` (une transaction par objet) ; si une FK référence l'objet → **conservé** (`kept`, procédure corrective §37) ; `reverted_at` posé. Statut `canceled`, event `ImportCancelled`. Le stock d'ouverture n'est pas supprimé (casserait le solde) → conservé |
| 38 | Migration sans interruption : import parallèle, gel minimal, delta final, double lecture, bascule contrôlée | **Backlog** (le pipeline est rejouable et idempotent, ce qui permet un delta final) |
| 39-41 | Migration locale / cloud / hybride (upload sécurisé, stockage temporaire chiffré, import asynchrone, suppression du fichier temporaire, delta local, synchro) | Le fichier est **stocké en base** (`import_job.content`, RLS entreprise, chiffrement au repos = infrastructure) et non sur un stockage temporaire ; `ResumeStalledAsync` (planificateur) fournit l'**asynchronisme** ; local/cloud/hybride suivent le mode de déploiement global (Phase 10/16) |
| 42-43 | Sécurité (fichiers chiffrés en transit, accès temporaire, scan, **secrets exclus**, données limitées, suppression contrôlée, audit, scope entreprise) ; confidentialité (**pas d'envoi vers un service externe** pour analyse sans autorisation, masquage, accès limité) | TLS (Phase 3) ; **tout le traitement est local** — profilage / détection de type / suggestion de mapping se font **dans le processus NEXORA**, aucun appel externe (§43) ; RLS entreprise sur les 6 tables ; permissions `migration.{view,manage}` ; `audit_log` ; taille de fichier plafonnée à 8 Mo. Scan antivirus des fichiers : infrastructure |
| 45 | API principales | `POST /api/v1/imports`, `GET /api/v1/imports/{id}`, `POST /imports/{id}/{profile,mapping,validate,simulate,execute,pause,resume,cancel}`, `GET /imports/{id}/{errors,reconciliation}`, `GET /api/v1/onboarding/checklist` + `GET /imports`, `GET /imports/{id}/{rows,events}`, `POST /imports/{id}/rows`, `GET /imports/target-fields/{kind}`, `POST /imports/suggest-mapping/{kind}`, `GET /api/v1/import-templates`, `POST /api/v1/onboarding/tasks` |
| 46 | Événements (`ImportCreated` … `MigrationReconciled`, `OnboardingStepCompleted`) | `MigrationEvents` (13 constantes) → `import_event` (`GET /imports/{id}/events`) |
| 47 | Tests | `MigrationImportTests` (11) + `MigrationEndpointsTests` (2) — cf. ci-dessous |
| 48 | Scénario complet `ANCIEN LOGICIEL → EXPORT → PROFILAGE → MAPPING → SIMULATION → CORRECTION → IMPORT → RAPPROCHEMENT → VALIDATION → NEXORA ONE ACTIF` | Couvert bout-en-bout par `Customer_import_creates_records_and_is_idempotent` + `Reconciliation_compares_source_and_nexora` + l'écran `MigrationWizardPage` |
| 49 | Critères de sortie | Sources CSV/JSON, 4 types importables, assistant, profilage, mapping (auto + gabarits), transformation, correspondance, doublons in-file, produits/clients/fournisseurs/stock, import test **sans impact**, prévisualisation, validation, erreurs ligne à ligne, batch + pause/reprise, idempotence, 5 modes, multi-entreprises (RLS), onboarding + score, rapprochement, audit, rollback, sécurité locale, formats CSV/JSON, API, événements : **livrés**. Ventes/achats/créances/dettes/comptabilité/documents, XLSX binaire, fusion floue, migration sans interruption : **backlog** |
| 50 | Étape suivante — Phase 40 (UX, design system & accessibilité) | hors périmètre |

## `V052` — contenu (6 tables, RLS entreprise + audit)

- **`import_job`** — `kind` (customers/suppliers/products/opening_stock), `source_type` (csv/json/xlsx/api/manual),
  `content` (fichier original) + `content_hash`, `status` (10 valeurs), `mode` (5 valeurs), `batch_size`
  (1–10000), compteurs (`total/processed/ok/warning/error/duplicate_rows`, `created/updated/skipped/rejected_count`),
  `profile` / `mapping` / `simulation` / `reconciliation` jsonb, `paused_at`, `finalized_at`,
  `UNIQUE (id, company_id)`.
- **`import_row`** — `row_number` (1-based), `source` jsonb (ligne brute), `dedup_key`, `match_type`
  (exact/probable/none), `status` (8 valeurs), `target_id`, `messages` jsonb, FK composite `(import_job_id,
  company_id)` `ON DELETE CASCADE`, `UNIQUE (import_job_id, row_number)`.
- **`import_record`** — `entity_type` / `entity_id` / `action` (created/updated) / `reverted_at` — trace pour rollback (§37).
- **`import_mapping_template`** — `UNIQUE (company_id, kind, name)`, `mapping` jsonb.
- **`import_event`** — ledger d'événements (§46).
- **`onboarding_task`** — `UNIQUE (company_id, task_key)`, `status` (pending/done/skipped) — acquittements manuels.
- `enable_company_rls` + `trg_audit_*` (5 tables mutables). Permissions **`migration.view`** (super/group
  admin, owner, manager, chief_accountant) et **`migration.manage`** (super/group admin, owner).
- **212 tables (+6), 182 RLS FORCE (+6)**, 52 migrations (V001–V052).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Migration/MigrationContracts.cs` |
| Application | `Nexora.Application/Migration/IMigration.cs` — `IImportService`, `IOnboardingService`, `IEntityImporter`, `MigrationEvents` ; `PermissionCatalog.Migration.{View,Manage}` |
| Infrastructure | `Migration/TabularParser.cs` (CSV/JSON + transforms), `ImportProfiler.cs`, `EntityImporters.cs` (`ImporterBase` + `Customer/Supplier/Product/OpeningStock` importers), `SqlImportService.cs` (pipeline), `SqlOnboardingService.cs` (checklist + score) ; DI ; hook `SchedulerRunner` (`imports.ResumeStalledAsync`) |
| API | `Nexora.Api/Controllers/ImportsController.cs` (`/api/v1/imports/*` + `/import-templates`), `OnboardingController.cs` (`/api/v1/onboarding/*`) |

### Points de conception

- **Pipeline générique, importeurs branchables** — `IEntityImporter { Kind, TargetFields,
  ValidateAsync, ProcessAsync }`. `ProcessAsync` applique **ou simule** (`ctx.DryRun`) — toute
  écriture est gardée par `if (!ctx.DryRun)`, donc la simulation ne touche jamais la base (§23),
  **sans transaction ni rollback**.
- **`SqlQueryRaw` pour le bloc SELECT réutilisé** (`JobSelect`) — un `const string` interpolé
  deviendrait un paramètre ; `SqlQueryRaw(JobSelect + " WHERE … {0}", p)` garde le SELECT en texte brut.
- **Détection de type stricte** — le profileur exige que la valeur brute *ressemble* au type
  (`^[+\-]?\d[\d.,]*$` pour décimal), pas seulement qu'elle soit coercible (`CLI-100` reste `text`).
- **Rollback prudent** — `CancelAsync` supprime objet par objet dans sa propre transaction ; une FK
  qui référence l'objet le fait basculer en « conservé » plutôt que d'échouer tout le rollback (§37).
- **Traitement 100 % local** (§43) — aucun appel réseau : parsing, profilage, suggestion de mapping
  s'exécutent dans le processus.

## Tests d'intégration — `MigrationImportTests` (11, `[Collection("finance")]`, entreprise c1)

| Test | § | Vérifie |
|---|---|---|
| `Csv_is_parsed_profiled_and_types_detected` | 6 | délimiteur `;`, `rowCount`, colonnes, types `email` / `decimal` / `text` (code alphanumérique) |
| `Suggest_mapping_matches_columns_to_target_fields` | 7, 33 | « Code Client » → `code`, « Raison sociale » → `name`, « Plafond » → `credit_limit` |
| `Validate_flags_missing_required_fields` | 25 | 2 lignes sans code / sans nom → `error_rows = 2`, messages « obligatoire » |
| `Simulation_makes_no_production_changes` | 23 | `wouldCreate = 2` **et** `COUNT(customer) = 0` après simulation |
| `Customer_import_creates_records_and_is_idempotent` | 11, 28 | 1er run → 2 créés ; 2ᵉ run (skip_duplicates) → 0 créé, 2 ignorés, toujours 2 en base |
| `Reject_duplicates_mode_rejects_the_repeated_row` | 29 | code répété dans le fichier → 1 créé, 1 rejeté |
| `Product_import_autocreates_category_and_unit` | 11 | catégorie « Migration Test Cat » + unité `BOX` créées ; `product_price` = 12,5 |
| `Opening_stock_posts_an_opening_movement` | 14 | produit importé puis stock → `stock_movement` `opening` `reference_type='import'` quantité 40 |
| `Cancel_rolls_back_created_entities` | 37 | 2 clients créés → `CancelAsync` → 0 client, statut `canceled` |
| `Reconciliation_compares_source_and_nexora` | 35 | 3 lignes source ↔ 3 intégrées, `allReconciled`, événement `MigrationReconciled` |
| `Onboarding_checklist_derives_items_and_score` | 32-34 | `company` = done, `notifications` = manuel, `Total = Items.Count`, `CompleteTaskAsync('tests')` → done |

## Tests API — `MigrationEndpointsTests` (2)

| Test | Vérifie |
|---|---|
| `Import_routes_require_authentication` | `/api/v1/imports` (GET/POST) + `/api/v1/onboarding/checklist` sans jeton → 401 |
| `Full_pipeline_over_http` | `create → profile → mapping → validate → simulate → execute` → `status: completed`, `reconciliation` 200, `cancel` 200 |

## Frontend

`src/migration/` — `migrationApi.ts` + **`MigrationWizardPage.tsx`** (route `/migration`, nav
« Migration & onboarding », icône `Truck`, gate `migration.view`) :

- **checklist d'onboarding** — progression %, score (données / mapping / qualité / compta + PRÊT),
  17 items avec pastille ✓ / – / ○, bouton « ok » pour les items manuels (`migration.manage`) ;
- **assistant d'import** — bandeau 7 étapes ; zone de collage CSV/JSON + type + mode ; tableau de
  **mapping** (champ NEXORA → colonne source + transformation, pré-rempli par la suggestion) ;
  boutons **Valider / Simuler / Lancer l'import final** ; **prévisualisation** (créations / MàJ /
  rejets / erreurs / doublons) ; **rapprochement** ; **Annuler (rollback)**.

Vitest `migrationApi.test.ts` (3) → **63 tests frontend**.

## Smoke API (port 5080, `direction@abc-group.demo`, `X-Company-Id` requis)

- CSV clients (2 valides + 1 doublon in-file), mode `skip_duplicates` : `create → profile`
  (types : `email` email, `limite` decimal, `code` text) → `suggest-mapping` (5 colonnes reliées) →
  `mapping → validate` (ok 2 / warn 1 / err 0 / dup 1) → `simulate` (`wouldCreate 2`) → **0 client
  en base avant `execute`** → `execute` (créé 2, ignoré 1) → **2 clients en base** → `reconciliation`
  (`allReconciled: true`) → events (8) → re-`execute` **409** → `cancel` → **0 client** (rollback).
- `GET /api/v1/onboarding/checklist` → progression 72,2 %, score `{data:75, mapping:0, quality:100,
  accounting:100, ready:false}`.

## Manques assumés (backlog)

- Importeurs ventes / achats / créances / dettes / comptabilité (§15-21), documents (§22) — le contrat `IEntityImporter` est prêt
- Parsing XLSX binaire (aujourd'hui : « exportez en CSV »)
- Fusion assistée des doublons + score de similarité flou (§10)
- Export CSV des lignes en erreur depuis l'écran (§26)
- Migration sans interruption (delta final, double lecture) (§38)
- Scan antivirus des fichiers, chiffrement au repos dédié (§42) — infrastructure
- Correspondance comptable ancien plan → NEXORA ONE avec contrôle débit = crédit (§20) via `IAccountingControls`
