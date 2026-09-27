# NEXORA — Phase 17 — Modèle de données PostgreSQL : état & validation

Référence : [`phase-17-modele-de-donnees-postgresql.md`](phase-17-modele-de-donnees-postgresql.md) ·
[`database/README.md`](../database/README.md).

Le schéma physique **existe déjà** (`database/migrations/` V001–V034, **161 tables**, ~300 FK,
~370 index, ~120 triggers, RLS FORCE sur 149) et est validé par `smoke_test.sql` +
**121 tests automatisés**. Cette phase confirme la couverture du modèle logique de référence,
fait la correspondance des noms canoniques, et **rend explicites au niveau base** deux règles de
cohérence critiques du §58.

## Correspondance modèle canonique (Phase 17) → schéma `nexora.*`

Le schéma NEXORA a été construit aux Phases 2, 5, 8-14 ; ses noms diffèrent parfois du modèle
canonique de cette phase (déjà noté dans [[nexora-database]]).

| Modèle canonique (§) | Table(s) `nexora.*` |
|---|---|
| `groups` (§6) | `business_group` (`code` → n/a, `default_currency_id`) |
| `companies` (§7) | `company` (`group_id` nullable, `default_currency_id`, `timezone`, `settings jsonb`, `main_activity_type`, `deployment_mode`) |
| `establishments` / `locations` / `cash_registers` / `devices` (§5) | `establishment` / `store` + `warehouse` + `inventory_location` / `cash_register` / `pos_terminal` + `sync_device` |
| `company_settings` / `establishment_settings` (§5, §48) | `setting` + `setting_definition` (périmètre `system/group/company/establishment`) + `company_branding` |
| `users` / `roles` / `permissions` / `role_permissions` (§8) | `app_user` / `role` (company-scoped) / `permission` / `role_permission` |
| `user_*_assignments` (§9) | `access_scope` (PLATFORM/GROUP/COMPANY/ESTABLISHMENT/STORE/WAREHOUSE/POS_TERMINAL) + `user_role` |
| `user_sessions` / `user_devices` (§8) | `user_session` (+ `app_user.security_epoch`) / `sync_device` |
| `products` + catalogue (§10) | `product` (`tax_configuration jsonb`), `product_category`, `brand`, `unit`, `unit_conversion`, `product_barcode`, `product_price`, `custom_field` + `custom_field_value` |
| `product_prices` (§12) | `product_price` (`price_type`, `amount`, `currency_id`, `valid_from`/`valid_to` — **historique non écrasé** : révision = clôture du `valid_to`) |
| `customers` + crédit (§13) | `customer` (`credit_limit`, `current_balance`, `dunning_opt_out`), `customer_group` |
| `suppliers` (§14) | `supplier` (`credit_limit`, `current_balance`) |
| stock (§15-17) | `warehouse` / `inventory_location` / `stock_balance` / `stock_movement` / `lot` / `stock_count` (+ lignes) / `stock_transfer` (+ lignes) / `stock_adjustment` |
| ventes (§18-21) | `sale` / `sale_line` (snapshot `description`,`cost_snapshot`) / `sale_promotion` / `sale_payment` / `sale_return` (+ `sale_return_line`) / `sale_service_context` (restaurant) |
| achats (§22-25) | `purchase_order` (+ lignes) / `goods_receipt` (+ lignes) / `supplier_invoice` (+ lignes) / `supplier_payable` |
| créances (§26) | `customer_receivable` + `payment_allocation` (N↔N paiements) |
| finance (§27-30) | `financial_account` (`cash/bank/mobile_money/card_processor/wallet`) / `cash_register` + `cash_session` + `cash_movement` / `financial_transaction` / `expense` (+ `expense_category`) / `budget` (+ `budget_line`) |
| comptabilité (§31-35) | `chart_of_accounts` / `account` (`account_type`, `parent_id`, `is_postable`) / `accounting_period` / `journal` / `journal_entry` / `journal_entry_line` (`debit`/`credit`/`base_debit`/`base_credit`/`cost_center_id`/`third_party_*`) / `account_mapping` / `cost_center` |
| consolidation (§37-38) | `consolidation_profile` / `consolidation_scope` / `group_account` + `account_group_mapping` (versionné) / `intercompany_transaction` + `intercompany_match` / `consolidation_run` (+ `_company`, `_line`) / `consolidation_adjustment` |
| paiements (§39-40) | `payment_method` / `payment` (`source_operation_uuid`) / `payment_allocation` / `integration_connector` / `payment_intent` (`idempotency_key`) / `webhook_event` |
| banques (§41) | `bank_statement` + `bank_statement_line` (`matched_transaction_id`) |
| intégrations (§42) | `integration_connector` (`credentials_ref` = pointeur coffre) / `webhook_event` / `outbound_webhook` (+ `_delivery`) |
| offline (§43) | `sync_device` / `sync_operation` (`operation_uuid` **UNIQUE**) / `sync_batch` / `sync_conflict` / `offline_policy` |
| audit (§44) | `audit_log` (**immuable**) / `fraud_signal` / `security_event` via `audit_log` / `approval_request` + `approval_action` / `approval_policy` |
| workflows (§45) | `workflow` + `workflow_run` / `automation` + `automation_run` / `config_version` (versionnement §48-49) |
| notifications (§46) | `notification` (interne) / `alert_rule` / `dunning_policy` |
| documents (§47) | `document` / `document_sequence` (numérotation contrôlée) / `company_branding` |
| paramétrage (§48) | `setting` / `setting_definition` (`is_sensitive`, `affects_future`) + `config_version` |

## Principes de conception (§2, §4, §10-11) — conformité

| Principe | État |
|---|---|
| PostgreSQL source transactionnelle | ✅ ; EF Core database-first, schéma dans `database/migrations/` |
| UUID stables ; numéros métier séparés | ✅ PK `uuid`, `document_sequence` pour les numéros |
| FK + contraintes d'intégrité | ✅ FK composites `(parent_id, company_id)` partout où le tenant compte |
| Montants NUMERIC, jamais float | ✅ domaines `money_amount numeric(19,4)`, `quantity numeric(20,6)`, `exchange_rate_value numeric(24,12)` |
| Dates UTC serveur | ✅ `timestamptz` ; `ReportingClock` = `current_date` DB |
| Company scope explicite | ✅ `company_id` NOT NULL sur les entités métier + RLS FORCE |
| Transactions ACID pour flux critiques | ✅ `UnitOfWork.ExecuteInTransactionAsync` (ré-entrant) ; trigger d'équilibre comptable **DEFERRABLE INITIALLY DEFERRED** (validé au COMMIT) |
| Historisation des opérations sensibles | ✅ `audit_row()` sur ~30 tables ; `product_price`/`account_group_mapping`/`config_version` versionnés |
| Migrations versionnées | ✅ `V001…V034` + `nexora.schema_migration` |
| Index issus des requêtes réelles | ✅ index `company_id + date`, `document_number`, `status` sélectif, références externes |
| Conventions §4 | ⚙️ `snake_case`, `id`, `<table>_id`, `created_at/updated_at`, `status` CHECK : ✅ ; `created_by/updated_by` : partiel ; `deleted_at` : rare (archivage logique via `status`) ; `currency_code` : NEXORA utilise `currency_id` (FK vers `currency`) plutôt qu'un code libre |

## Règles de cohérence critiques (§58) — où elles sont tenues

| Règle | Contrainte / mécanisme |
|---|---|
| Vente validée ⇒ entreprise | `sale.company_id NOT NULL` + FK `company` |
| Ligne de vente ⇒ vente | `sale_line.sale_id NOT NULL` + FK composite `(sale_id, company_id)` `ON DELETE CASCADE` |
| **Paiement ⇒ source / contexte financier** | **`payment_context_chk` (V034)** : `financial_account_id OR customer_id OR supplier_id OR cash_session_id` |
| **Mouvement de stock ⇒ cause identifiable** | **`stock_movement_cause_chk` (V034)** : `reference_type IS NOT NULL` |
| Dette fournisseur ⇒ entreprise | `supplier_payable.company_id NOT NULL` + FK |
| Créance ⇒ entreprise | `customer_receivable.company_id NOT NULL` + FK |
| Écriture ⇒ période + journal | `journal_entry.journal_id NOT NULL` + `accounting_period_id NOT NULL` + FK composites |
| Consolidation ne modifie pas les sources | architectural : écrit dans `consolidation_run_line`, jamais dans `journal_entry` |
| Événement offline ⇒ clé unique | `sync_operation_uuid_uk UNIQUE (operation_uuid)` |
| Écriture validée équilibrée | trigger `journal_entry_line` différé (débit total = crédit total au COMMIT, ≥ 2 lignes) |
| Période clôturée ⇒ pas d'écriture normale | trigger V019 (`allow_closed_period` requis) |
| Vente / écriture / audit validés ⇒ pas de suppression physique | `forbid_hard_delete_validated()` (V019, **corrigé en V034** : lecture `to_jsonb(OLD)->>'status'` robuste sur les tables sans colonne `status`) ; `audit_log` immuable |

## Ce qui est livré dans cette phase

- `docs/phase-17-modele-de-donnees-postgresql.md` — spec verbatim.
- **`database/migrations/V034__data_model_integrity.sql`** :
  - `stock_movement_cause_chk` — tout mouvement de stock porte un `reference_type` (§16, §58) ;
  - `payment_context_chk` — tout paiement est rattaché à un tiers ou un compte financier (§30, §58) ;
  - correction de `forbid_hard_delete_validated()` : les tables « toujours verrouillées »
    (`stock_movement`, `customer_receivable`, `supplier_payable`) n'ont pas de colonne `status` —
    l'ancienne fonction échouait sur un message technique au lieu du message métier de blocage.
- **`Nexora.IntegrationTests/DataModelIntegrityTests.cs`** — 7 tests **au niveau base** (§58-59).
- `docs/phase-17-implementation.md` — ce document.

Aucune écriture existante (domaine, `smoke_test.sql`, `demo_abc_group.sql`) ne viole les
nouvelles contraintes — vérifié avant application (`SELECT count(*) … WHERE … IS NULL` = 0).

## Vérifications exécutées

**Tests d'intégration `DataModelIntegrityTests` — 7/7** :

| Scénario (§58-59) | Vérifié |
|---|---|
| Mouvement de stock sans cause | rejeté — `23514` `stock_movement_cause_chk` |
| Paiement sans aucun contexte | rejeté — `payment_context_chk` |
| Ligne de vente d'une autre entreprise | rejetée — `23503` sur la FK composite `(sale_id, company_id)` |
| Double `operation_uuid` offline | rejeté — `23505` `sync_operation_uuid_uk` |
| Écriture sans période ni journal | rejetée — `23502` not-null |
| Suppression physique d'un mouvement validé | rejetée — message « suppression physique interdite » |
| Entreprise indépendante (sans groupe) | acceptée — `group_id NULL` |

Scénarios §59 déjà couverts ailleurs : isolation `company_id` (dès la Phase 3), vente + stock +
paiement + écriture (`PosSalesTests`, `FinancialEngineTests`), vente/achat à crédit + créance/
dette (`PosSalesTests`, `CatalogInventoryTests`), remboursement + retour stock (`PosSalesTests`),
inventaire (`CatalogInventoryTests`), intercompany + rapprochement + consolidation
(`GroupConsolidationTests`), transaction offline envoyée 2× + idempotence (`OfflineSyncTests`),
période clôturée (`FinancialEngineTests`), audit (`SecurityAuditTests`).

**Total backend : 121 tests verts** (16 unitaires + 2 API + 103 intégration), build Debug **et**
Release 0 warning. DB : **34 migrations (V001–V034)**, 161 tables, 149 RLS. Rebuild + `smoke_test`
+ démo (rejouée 2×) OK. `tsc` strict + `vite build` OK.

## Couverture des critères de sortie (§60)

Tous **✅** : schéma logique validé, entités / relations / contraintes / index / migrations
définis et **en production** (`database/migrations/`), multi-entreprises (RLS FORCE + FK
composites), POS / stock / achats / ventes / crédit / finance / comptabilité / consolidation /
paiements / intégrations / offline / audit / workflows / documents / paramétrage — tous
matérialisés en tables et éprouvés par 121 tests + `smoke_test.sql`. Partitionnement (§54) et
RLS applicative-vs-DB (§55) : NEXORA a choisi **RLS FORCE systématique** (au-delà de la
recommandation de la spec) ; le partitionnement reste à activer selon les métriques de production.

## Suite — Phase 18

Architecture API .NET / ASP.NET Core : contrats, modules, endpoints, DTO, services applicatifs,
autorisation, validation, gestion des erreurs et flux métier. Le back-end existe déjà
(`Nexora.{Api,Application,Domain,Infrastructure,Contracts}`, ~30 contrôleurs `/api/v1`) ; la
Phase 18 en produira la spécification de référence.
