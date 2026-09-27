# Phase 32 — Configuration & Adaptabilité — implémentation

**Statut : livré et vérifié.** Migration `V045`. 7 tests d'intégration (`ConfigAdaptabilityTests`),
4 tests frontend (`configApi.test.ts`). **209 tests backend** verts (16 unit + 7 API +
186 intégration), Debug **et** Release, 0 warning. 35 tests frontend.

Le moteur de configuration existe déjà et est **validé** par cette phase :

- **Phase 8** (`V027`) — `company_activity`, `company_module` (activer / désactiver §8-9),
  `sector_module_preset` (modules recommandés par activité), `terminology_override` (§7, §10),
  `custom_field` + `visibility` jsonb (champs conditionnels §11-12) / `custom_field_value`
- **Phase 14** (`V033`) — `setting` / `setting_definition` (config résolue par périmètre §7-8, §32-38),
  `role` personnalisé + `ICustomRoleService` (§23), `IWorkflowService` + moteur de règles +
  `.TestAsync` (workflows / règles / approbations §20-22, simulation §51),
  `config_version` (draft / active / superseded, snapshot + diff + activation §44-45, §53)
- **Phase 9** — `ICompanyProvisioningService` (onboarding §3) ; **Phase 22** — scopes d'accès (§24-25)
- **Phase 23** (`V036`) — unités, catégories, prix HT/TTC, taxes, listes tarifaires (§13-18)
- **Phase 30** (`V043`) — `document_sequence` + `next_document_number` (numérotation §19)
- **Phase 13** (`V032`) — `company_branding` (documents / thème §37)
- **Phase 10** — mode local / cloud / hybride (`device` + `offline_policy`, §40-43)

La Phase 32 ajoute les deux briques qui manquaient au §6, §46-49 et §52 :

1. **Profils sectoriels réutilisables** (`sector_profile`) — un *bundle* versionné de configuration
   (modules + champs + terminologie + réglages + numérotation) applicable à une entreprise
   **sans changer de version logicielle**, avec snapshot `config_version` et passage des garde-fous.
2. **Transfert de configuration** (`config_transfer`) — export / import / clonage d'une configuration
   d'entreprise à l'autre, **jamais de secrets, soldes ni historique**.
3. **Garde-fous** (`IConfigGuardrails`) — contrôle de cohérence avant publication.

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-2 | Même moteur, config par entreprise, **sans modifier le code métier** ; données compatibles même module désactivé ; config importante versionnée & auditée ; modifiable après lancement | `sector_profile.definition` jsonb appliqué par `ConfigApplier` sur les tables **existantes** (`company_module`, `custom_field`, `terminology_override`, `setting`, `document_sequence`) ; chaque application prend un `config_version` (§44) ; `trg_audit_*` sur `sector_profile` & `config_transfer` |
| 3 | Onboarding (créer → type → taille → devise → structure → fonctionnalités → règles → utilisateurs → validation) | `ICompanyProvisioningService` (Phase 9) ; l'étape « fonctionnalités » = application d'un profil sectoriel (`ISectorProfileService.ApplyAsync`) ; l'étape « validation » = `IConfigGuardrails.ValidateAsync` |
| 4-5 | Type & taille d'entreprise **orientent** la config initiale sans verrouiller une version | `sector_profile.sector_type` + `key` ; `ListAsync` renvoie les 5 modèles système ; le type ne restreint aucune capacité (les modules non listés restent activables via `/sectors`) |
| 6, 49 | Profil sectoriel (pharmacie / restaurant / supermarché / grossiste / services) ; marketplace interne de modèles | **`sector_profile`** — `company_id IS NULL` = modèle système/marketplace, `company_id` renseigné = profil d'entreprise. `V045` seed **5 modèles système** (`pharmacy`, `restaurant`, `wholesale`, `retail`, `services`). `GET /api/v1/sector-profiles` |
| 7-9 | Configuration sans code : activer / masquer / configurer / ordonner modules & fonctionnalités | `ConfigApplier` upsert `company_module` (ON CONFLICT `company_module_uk`) ; masquage d'interface & ordre = `custom_field.visibility` + préférences (Phases 8/13) — inchangé |
| 10 | Menus & navigation configurables | `terminology_override` (libellés) + `company_module` (visibilité) — socle Phase 8, réutilisé par le profil |
| 11-12 | Champs personnalisés & conditionnels | `ConfigApplier` crée `custom_field` (valide `entity_type` ∈ 10 entités, `data_type` ∈ 7 types, sinon *warning* et skip) ; `options` jsonb pour les listes ; `visibility` (Phase 8) pour le conditionnel |
| 13-18 | Unités, catégories, produits configurables, prix, taxes, devises | Phase 23 (`CatalogPricingEngine`, `product_price.tax_included`, `price_list`) + `currency` — le profil peut pousser des `setting` (ex. `pos.max_discount_percent`) mais ne duplique aucun barème |
| 19 | Numérotation (séquences, préfixes, RAZ, format) | `ProfileNumberingSpec(DocumentType, Prefix)` → `ConfigApplier` upsert `document_sequence` (année courante, `include_year`). **Les modèles système ne portent pas de numérotation** (spécifique entreprise) ; le garde-fou `numbering_unique` détecte les collisions de préfixe |
| 20-22 | Workflows / règles / approbations configurables & testables | `IWorkflowService` + `.TestAsync` (Phase 14) — validés, non redéveloppés ; `POST /configurations/{versionId}/publish` & `/rollback` réutilisent `IConfigVersionService.ActivateAsync` |
| 23-25 | Rôles personnalisés & périmètres d'accès (dont comptable en chef) | `ICustomRoleService` (Phase 14) + scopes (Phase 22) — l'export embarque les **rôles non-système** avec leurs permissions (`ExportedRoleDto`) ; l'import/clone les recrée |
| 26-31 | Config POS / restaurant / pharmacie / supermarché / grossiste / services | Portées par les `definition` des 5 modèles système : modules + champs (`table_number`, `covers`, `molecule`, `insurance_number`, `carton_qty`, `billing_unit`…) + terminologie (`customer→Patient`, `sale→Addition`…) + réglages (`stock.expiry_alert_days`, `stock.block_expired_sales`) |
| 32-38 | Comptabilité / finance / stock / clients / fournisseurs / documents / reporting configurables | `setting` résolu par périmètre (Phase 14) ; le profil pousse des `setting` **seulement si `setting_definition` existe** (sinon *warning*) ; mappings comptables = `account_mapping` (Phase 4), contrôlés par le garde-fou `required_mappings` |
| 39 | Notifications configurables | `setting` + `notification_rule` (Phase 14) — inchangé |
| 40-43 | Local / cloud / hybride | `device` + `offline_policy` (Phase 10) — inchangé ; `config_transfer` fonctionne à l'identique quel que soit le mode |
| 44-45 | **Configuration versionnée** & environnements | `ISectorProfileService.ApplyAsync` appelle `IConfigVersionService.SnapshotAsync("sector_profile", …)` **avant** d'appliquer → rollback possible. Domaine `"sector_profile"` ajouté au tableau `Domains` de `ConfigVersionAndTasks` |
| 46 | **Import configuration** | `IConfigTransferService.ImportAsync(ImportConfigRequest{Bundle, DryRun})` → garde-fous puis `ApplyBundleAsync`. `POST /api/v1/configurations/import` |
| 47 | **Export configuration** (structure lisible, sans secrets) | `IConfigTransferService.ExportAsync` → `ConfigBundleDto` (`schemaVersion=1.0`) : modules, champs actifs, terminologie, **réglages non sensibles** (`JOIN setting_definition d … d.is_sensitive = false`, périmètres `company`/`establishment`), numérotation (préfixe non vide), rôles personnalisés + permissions. `GET /api/v1/configurations/export` |
| 48 | **Clonage** vers une autre entreprise autorisée, **sans données transactionnelles** | `CloneAsync(CloneConfigRequest{TargetCompanyId})` : refuse la cible = source (`config.clone_self`, 422), refuse hors périmètre (`config.clone_scope`, 403 — `tenant.CompanyIds`), sinon `Export(source)` + `ApplyBundleAsync(target)`. Aucune écriture sur `sale`/`payment`/`journal_entry`. `POST /api/v1/configurations/clone` |
| 50-51 | Assistant de configuration & **simulation** (dry-run) | `PreviewAsync` (profil) + `ApplyProfileRequest(DryRun:true)` + `ImportConfigRequest(DryRun:true)` : calculent modules/champs/termes/réglages **à créer** sans rien écrire ; `ProfilePreviewItemDto.AlreadyPresent` distingue le neuf de l'existant |
| 52 | **Garde-fous** (devise, mappings, séquences, modules, champs, approbateurs réels) | **`IConfigGuardrails.ValidateAsync`** → `GuardrailReportDto` : 5 contrôles — `currency_valid` (**fail**), `required_mappings` (**fail** : `sales_revenue, sales_tax_output, accounts_receivable, accounts_payable, cogs, inventory, cash, bank`), `numbering_unique` (**warn**), `custom_fields_valid` (**warn** : liste sans option), `open_accounting_period` (**warn**). `CanPublish = aucun fail`. `ApplyAsync` **bloque** si `!CanPublish && !DryRun` → `config.guardrails_failed` (409). `GET /api/v1/configurations/validate` |
| 53 | **Audit** (qui, quand, quoi, avant / après, version, environnement) | `config_transfer` (kind `export`/`import`/`clone`/`apply_profile`, `source_*`, `dry_run`, `config_version_id`, `summary` jsonb) + `trg_audit_config_transfer` & `trg_audit_sector_profile` (Phase 11) ; `HistoryAsync` → `GET /api/v1/configurations/history` |
| 54 | API principales | **`GET /api/v1/sector-profiles`**, **`GET /sector-profiles/{key}`**, **`POST /sector-profiles/{key}/preview`**, **`POST /sector-profiles/{key}/apply`** ; **`GET /api/v1/configurations/validate`**, **`/export`**, **`POST /import`**, **`POST /clone`**, **`GET /history`**, **`GET /{domain}/versions`**, **`POST /{versionId}/publish`**, **`POST /{versionId}/rollback`**. Le reste (`company-settings`, `custom-fields`, `workflows`, `rules`, `approval-policies`, `document-templates`, `numbering-series`) est servi par `/admin/*` & `/sectors/*` (Phases 8/14) — **écart de nommage assumé** |
| 55 | Événements (`ConfigurationPublished`, `SectorProfileApplied`, …) | Journalisés via `config_transfer` + `config_version` + `audit_log` ; pas de bus d'événements dédié (backlog commun aux phases 16-32) |
| 56 | Tests | `ConfigAdaptabilityTests` (7) — cf. ci-dessous |
| 57 | Scénario restaurant (active tables + cuisine + pourboire, conserve stock/POS/finance/compta, ajoute livraison, change de devise, ajoute une succursale) | Couvert par `Apply_pharmacy_profile_…` + `Preview_reports_items_…` (restaurant) + `Clone_copies_config_…` ; le changement de devise / succursale relève des Phases 9/28, inchangées |
| 58 | Critères de sortie | Onboarding (Ph.9), profils secteur (**neuf**), modules/menus/champs/règles/workflows/approbations/rôles/scopes (Ph.8/14/22), configs métier (Ph.14/23), local/cloud/hybride (Ph.10), versioning (Ph.14 + domaine `sector_profile`), import/export/clonage (**neuf**), modèles (**neuf**), simulation (**neuf**), garde-fous (**neuf**), audit (Ph.11 + `config_transfer`), API & tests (**neuf**) |

## `V045` — contenu

- **`sector_profile`** — `company_id` **nullable** (NULL = modèle système), `key`, `name`, `sector_type`,
  `version`, `status` (`draft`/`published`/`archived`), `is_system`, `description`, `definition` jsonb
  (`{ modules[], customFields[], terminology[], settings[], numbering[] }`).
  `sector_profile_key_uk UNIQUE NULLS NOT DISTINCT (company_id, key)`.
  RLS **custom** : `USING (rls_bypass() OR company_id IS NULL OR rls_allows_company(company_id))`,
  `WITH CHECK (rls_bypass() OR rls_allows_company(company_id))` — les modèles système visibles de tous,
  jamais modifiables par une entreprise.
- **`config_transfer`** — `company_id`, `kind` (`export`/`import`/`clone`/`apply_profile`),
  `source_company_id`, `source_profile_key`, `dry_run`, `config_version_id`, `summary` jsonb,
  `created_by`. `enable_company_rls` + `trg_audit_config_transfer`.
- **5 modèles système** seedés (`ON CONFLICT DO NOTHING`) : pharmacie, restaurant, grossiste,
  détail, services. **Aucune numérotation** dans les modèles (spécifique entreprise).
- Permissions `config.apply_profile`, `config.transfer`, `config.validate`
  (super_administrator / group_administrator / owner_direction).
- **177 tables (+2), 165 en RLS (+2)**, 45 migrations (V001–V045).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Configuration/ConfigContracts.cs` — `SectorProfileDto`, `SectorProfileDefinitionDto`, `ProfilePreviewDto`, `ApplyProfileRequest/ResultDto`, `ConfigBundleDto`, `ExportedSettingDto/RoleDto`, `CloneConfigRequest`, `ImportConfigRequest`, `ConfigTransferDto`, `GuardrailDto/ReportDto` |
| Application | `Nexora.Application/Configuration/IConfiguration.cs` — `ISectorProfileService`, `IConfigTransferService`, `IConfigGuardrails` |
| Infrastructure | `Nexora.Infrastructure/Configuration/SqlConfiguration.cs` — `ConfigApplier` (upserts idempotents), `SqlSectorProfileService`, `SqlConfigTransferService`, `SqlConfigGuardrails` ; `Administration/ConfigVersionAndTasks.cs` → `Domains += "sector_profile"` ; DI dans `Infrastructure/DependencyInjection.cs` |
| API | `Nexora.Api/Controllers/SectorProfilesController.cs`, `ConfigurationController.cs` ; `Nexora.Application/Security/PermissionCatalog.cs` → `Permissions.Config.{ApplyProfile, Transfer, Validate}` |

**Idempotence** — `ConfigApplier` teste l'existence avant chaque insert (module activé, champ
`(entity_type, field_key)`, override `(term_key, locale, label)`, `setting_definition`) : une
seconde application ne recrée rien (`FieldsCreated = 0`). Les inserts utilisent `ON CONFLICT ON
CONSTRAINT … DO UPDATE` sur les contraintes existantes.

**Transaction** — `ApplyAsync` / `ApplyBundleAsync` passent par `uow.ExecuteInTransactionAsync` :
snapshot + upserts + ligne `config_transfer` dans **une** transaction ; toute `PostgresException` /
`DbUpdateException` / `InvalidOperationException` → `Result` `config.apply_failed`.

## Tests d'intégration — `ConfigAdaptabilityTests` (7, `[Collection("finance")]`)

| Test | § | Vérifie |
|---|---|---|
| `Apply_pharmacy_profile_activates_modules_and_creates_fields` | 6, 9, 11-12 | après application : `company_module` pharmacy activé, `custom_field` `customer.insurance_number`, `terminology_override` `customer`→« Patient », `setting` `stock.expiry_alert_days`=60 ; ré-application → `FieldsCreated = 0` (assertions **d'état final**, robustes à l'ordre des tests) |
| `Profile_apply_is_versioned_and_logged` | 44, 53 | `config_version` domaine `sector_profile` créé, `config_transfer` kind `apply_profile` avec `source_profile_key='wholesale'` |
| `Preview_reports_items_without_changing_anything` | 50-51 | `PreviewAsync("restaurant")` liste le module `restaurant` ; le nombre de `custom_field` est inchangé |
| `Export_bundle_excludes_sensitive_settings` | 47-48 | `ExportAsync` → `schemaVersion=1.0`, contient le module `pharmacy` & le champ `insurance_number` ; **chaque** réglage exporté a `setting_definition.is_sensitive = false` |
| `Clone_copies_config_to_another_authorized_company` | 48 | scope `[c1, c2]` : `CloneAsync(c2)` → c2 reçoit le module `pharmacy`, **le nombre de ventes de c2 est inchangé**, `config_transfer` kind `clone` ; `CloneAsync(c1)` → `config.clone_self` |
| `Guardrails_block_on_a_missing_core_mapping` | 52 | suppression du mapping `inventory` → `required_mappings` = `fail`, `CanPublish = false`, `ApplyAsync("retail")` → `config.guardrails_failed` ; mapping restauré en `finally` |
| `Guardrails_pass_on_the_demo_company` | 52 | `currency_valid` & `required_mappings` = `pass`, `CanPublish = true` |

## Frontend

`src/sectors/` — **`configApi.ts`** (`listSectorProfiles`, `previewSectorProfile`, `applySectorProfile`,
`validateConfig`, `exportConfig`, `cloneConfig`, `configHistory`) + deux sections ajoutées à
**`SectorSettingsPage.tsx`** (route `/sectors`, nav « Secteurs & modules ») :

- **Profils sectoriels** — sélection d'un profil, **Prévisualiser** (tableau type / élément /
  « nouveau » vs « déjà présent » + garde-fous bloquants), **Appliquer** (désactivé sans
  `config.apply_profile`, affiche modules/champs/termes/réglages appliqués + id du snapshot + *warnings*).
- **Garde-fous** — bouton **Vérifier** (désactivé sans `config.validate`) → checklist
  ✓ ok / ⚠ alerte / ✕ échec + verdict « publication autorisée / bloquée ».

Vitest `configApi.test.ts` (4 : route de liste, `preview` en POST sans corps, `apply` avec `dryRun`,
route `validate`) → **35 tests frontend**.

## Smoke API (port 5080, `direction@abc-group.demo`)

- `GET /sector-profiles` → 5 modèles système ; `POST /sector-profiles/pharmacy/preview` → items
  `alreadyPresent` corrects ; `GET /configurations/validate` → 5 `pass`, `canPublish=true` ;
  `POST /sector-profiles/restaurant/apply {dryRun:true}` → `modulesApplied=2, fieldsCreated=3`,
  rien écrit ; `POST /sector-profiles/pharmacy/apply {}` → `configVersionId` non nul ;
  `GET /configurations/export` → réglages non sensibles seulement ; `GET /configurations/history` →
  lignes `apply_profile`.
- `POST /configurations/clone` : cible = source → `422 config.clone_self` ; cible hors périmètre →
  `403 config.clone_scope`.
- Caissier (`caisse@abc-group.demo`) : **403** sur `/sector-profiles`, `/sector-profiles/{k}/apply`,
  `/configurations/{validate,export,clone}`.

## Manques assumés (backlog)

- Bus d'événements `SectorProfileApplied` / `ConfigurationPublished` (backlog commun phases 16-32) (§55)
- Marketplace interne de modèles avec partage inter-groupes & notation (§49) — le socle
  `sector_profile` (`company_id NULL`) est en place, l'UI de publication d'un profil d'entreprise
  vers la marketplace reste à faire
- Assistant d'onboarding pas-à-pas guidé (§50) — les briques (`preview`, `apply`, `validate`) existent,
  l'enchaînement UI dédié est un écran à part
- Éditeur visuel de `sector_profile.definition` (création / versionnement d'un profil d'entreprise) —
  seule l'**application** est exposée ; la création passe par SQL / seed
- Environnements de configuration (sandbox / prod) distincts (§45)
