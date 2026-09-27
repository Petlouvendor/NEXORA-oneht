# NEXORA — Base de données PostgreSQL

Schéma physique dérivé de [`docs/phase-2-modele-de-donnees.md`](../docs/phase-2-modele-de-donnees.md).
Cible : **PostgreSQL 14+** (développé et validé sur PostgreSQL 18).

## Contenu

```
database/
├── migrations/     V001..V078 — schéma versionné, appliqué dans l'ordre
├── seeds/          demo_abc_group.sql — données de démonstration persistantes
│   └── recette/    01_supermarche_config.sql, verif_jeu_recette.sql — jeu de recette (Phase 55)
└── scripts/
    ├── apply.sh    applique les migrations non encore jouées (idempotent)
    ├── rebuild.sh  DROP + CREATE + apply  (DESTRUCTIF, dev uniquement)
    ├── smoke_test.sql  test fonctionnel du modèle (transaction annulée)
    ├── backup.sh   sauvegarde logique (pg_dump custom + SHA-256 + enregistrement au registre §36)
    └── recette.sh  construit la base de RECETTE `nexora_recette` (schéma + démo + jeu généré) — refuse `nexora`
```

La base `nexora` sert de **modèle** aux tests d'intégration : n'y chargez jamais de données de
recette. Le jeu de recette (Phase 55) vit dans sa propre base, voir
[`docs/phase-55-jeu-de-donnees-recette.md`](../docs/phase-55-jeu-de-donnees-recette.md).

## Démarrage rapide

```bash
export PGPASSWORD=...            # superutilisateur postgres
cd database/scripts

./rebuild.sh                     # (re)crée la base « nexora » et applique tout
psql -h localhost -U postgres -d nexora -f smoke_test.sql   # vérifie le modèle
psql -h localhost -U postgres -d nexora -f ../seeds/demo_abc_group.sql
```

Sur une base existante, en incrémental :

```bash
./apply.sh                       # ne rejoue que les migrations manquantes
```

Chaque migration jouée est enregistrée dans `nexora.schema_migration`
(version, fichier, checksum SHA-256, date, rôle).

### Sans PostgreSQL local

```bash
docker compose -f infrastructure/docker-compose.yml up -d
PGPASSWORD=nexora PGUSER=nexora ./scripts/rebuild.sh nexora
```

## Conventions

| Élément | Choix |
|---|---|
| Schéma | tout dans `nexora` ; extensions (`pgcrypto`, `citext`, `btree_gist`) dans `public` |
| Clé primaire | `uuid` + `gen_random_uuid()` |
| Horodatage | `timestamptz`, `created_at` / `updated_at` (trigger `set_updated_at`) |
| Montants | domaine `nexora.money_amount` = `numeric(19,4)` — jamais `float` |
| Quantités | `nexora.quantity` = `numeric(20,6)` (produits au poids) |
| Taux de change | `nexora.exchange_rate_value` = `numeric(24,12)` |
| Statuts / énums | colonnes `text` + `CHECK (... IN (...))` — évolutives sans migration lourde |
| Isolation | `company_id` sur chaque table métier + FK composites `(parent_id, company_id)` |
| Soldes | colonnes générées (`remaining_amount`, `quantity_available`, `amount_due`…) + `CHECK ≥ 0` |

### Écarts assumés vs modèle logique

- `group` → **`business_group`**, `user` → **`app_user`** (mots réservés SQL).
- `category` → **`product_category`** ; `sales_order`/`sale` → **`sale`** (+ `sale_line`, `sale_payment`, `sale_return`).
- Chaque table enfant porte une copie `company_id` **non nulle** et une FK composite vers
  le parent `(id, company_id)` : impossible de mélanger deux entreprises sur une même
  transaction (§31). `product_category` / `brand` sont propres à l'entreprise (pas de
  variante globale au niveau schéma — géré par copie de modèles à la création).

## Row-Level Security (§34)

RLS `FORCE` activée sur 248 tables (sur 287, état V078). Les 17 tables **plateforme** de `V051` (facturation
NEXORA, transverse aux locataires — cf. plus bas) n'ont **pas** de RLS entreprise : l'accès y est
filtré par les permissions `platform.*`. Les 6 tables `import_*` / `onboarding_task` de `V052` sont
en RLS entreprise. Les 3 tables de personnalisation UX de `V053` (`dashboard_layout`, `saved_filter`,
`ui_shortcut`) sont en **RLS personnalisée** (les miens / les partagés de l'entreprise / les défauts
d'entreprise), doublée d'un filtre explicite côté service (défense en profondeur §56). Les 4 tables
d'exploitation de `V054` (`job_definition`/`job_run`/`feature_flag`/`maintenance_window`) sont des
tables **plateforme sans RLS** (comme `deployment`) ; ses 3 autres tables (`retention_policy`/
`purge_run`/`data_correction_request`) ont la même RLS personnalisée (défaut système visible de
tous + surcharge par entreprise). Les 5 tables d'intelligence de `V056` (`recommendation`,
`entity_score`, `assistant_query`, `task`) sont en RLS entreprise standard ; `intelligence_rule`
(`company_id` nullable = gabarit système, même principe que `sector_profile` V045) utilise aussi
`enable_company_rls` directement — la fonction générique traite déjà `company_id IS NULL` comme
visible de tous, la couche service se charge seule d'empêcher un appelant tenant de créer une
ligne système (aucune route API ne le permet, seule la migration en amorce). Les 5 tables terrain
de `V057` (`attachment`, `delivery_route`/`delivery_stop`, `field_intervention`, `crash_report`)
sont toutes en RLS entreprise standard. Les 6 tables GED de `V058` (`document`, `document_version`,
`document_link`, `document_share`, `document_signature_request`, `document_signer`) sont en RLS
entreprise standard ; sa 7ᵉ table `document_retention_policy` (`company_id` nullable = gabarit
système, même principe que `sector_profile`/`intelligence_rule`) utilise aussi `enable_company_rls`
directement. Les 2 tables entreprise de `V059` (`app_installation`, `api_key_daily_usage`) sont en
RLS entreprise standard ; sa 3ᵉ table `marketplace_app` est une table **plateforme sans RLS**, même
principe que `platform_plan` (V051) — un seul catalogue pour tout NEXORA ONE, la visibilité par
statut est un filtre applicatif, pas une politique RLS. La table entreprise de `V060`
(`emergency_access_grant`) est en RLS entreprise standard ; ses 3 tables plateforme
(`security_finding`, `security_certificate`, `compliance_control`) sont sans RLS, même principe.
La table neuve de `V062` (`dr_test`) est en RLS entreprise standard — Phase 48 n'ajoute aucune table
plateforme. Les migrations
`V027`–`V033` re-appliquent la
politique d'isolation entreprise à toute table métier ajoutée après `V020` ; `V028` ajoute
l'isolation par périmètre groupe (consolidation), `V030` l'isolation par utilisateur
(`user_session`, `step_up_challenge`), `V031` l'isolation des connecteurs / intentions de
paiement / webhooks, `V032` `user_preference` + `company_branding`, `V033` les tables
d'administration (`setting`, `workflow(_run)`, `automation(_run)`, `dunning_policy`,
`config_version`), `V034` deux contraintes de cohérence §58 (`stock_movement_cause_chk`,
`payment_context_chk`) + correction du garde-fou anti-suppression, `V035` les fenêtres
de validité des affectations de rôle (`user_role.valid_from/valid_until/reason/revoked_at/
revoked_by` — affectations temporaires & révocables, Phase 22), `V036` la table
`price_list` (listes tarifaires) + `product_price.tax_included/price_list_id` +
`product.plu_code/short_name` + workflow `product.status` + `nexora.search_norm()`
(recherche tolérante `unaccent`/`pg_trgm`, Phase 23), et `V037` le cycle de vie des lots
(`lot.blocked_at/reason/by`, `recall_reference`, `supplier_id`, statut `blocked`, audit
`trg_audit_lot`) + le détail des emplacements (`inventory_location.code/zone/aisle/shelf/bin`,
12 types) + les paramètres d'expiration (Phase 24), `V038` les commandes clients
(`sales_order` + `sales_order_line`, `sale.sales_order_id`, Phase 25), et `V039` le
rapprochement 3-way (`supplier_invoice.match_status/payment_blocked/match_detail`, état
commande `sent`, anti-doublon de référence facture, audit achats, Phase 26), et `V040` le
recouvrement client (`customer_promise`, `customer_dispute`, `customer_block`,
`receivable_write_off` ; `customer_receivable.status` += `IN_DISPUTE`/`WRITTEN_OFF` ;
clé de mapping `bad_debt`, Phase 27), et `V041` le cockpit de trésorerie
(`treasury_suspense_item` — file des sommes non identifiées / non affectées §32 ;
`financial_close` — clôture financière avec instantané des contrôles §38 ; réglages
`treasury.liquidity_horizon_days` / `suspense_max_age_days` / `min_cash_alert` ;
permissions `finance.close_period` / `finance.manage_suspense`, Phase 28), et `V042` la
comptabilité générale (`journal_entry.origin` — system / manual / opening / closing / reversal /
adjustment ; `accounting_year_close` — clôture d'exercice avec instantané des contrôles §18,
résultat → `current_year_earnings` ; journaux `AN` / `CL` ; permission `accounting.carry_forward`,
Phase 29), et `V043` la facturation & les documents commerciaux (`quote` / `quote_line` — devis
avec cycle brouillon → envoyé → accepté → converti §4-5 ; `issued_document` += snapshot `content`
jsonb + `content_hash` SHA-256 + `verification_code` (QR §27-28) + langue + annulation /
remplacement ; `document_send` — journal d'envoi §36 ; permissions `documents.*`, Phase 30),
et `V044` la couche de confiance du reporting (`analytics_snapshot` — photo reproductible
d'une réconciliation KPI §46 ou d'un contrôle qualité des données §45, résultat figé en jsonb
+ version de calcul §44 ; permission `reporting.data_quality`, Phase 31),
et `V045` la configuration & l'adaptabilité (`sector_profile` — bundle versionné de config
[modules + champs + terminologie + réglages], `company_id NULL` = modèle système, 5 modèles
seedés, RLS custom §6, §49 ; `config_transfer` — journal export / import / clonage / application
de profil §46-48, §53 ; permissions `config.apply_profile` / `config.transfer` / `config.validate`,
Phase 32), et `V046` la MFA & le durcissement de la connexion (`app_user` += `mfa_pending_secret` /
`mfa_enrolled_at` / `failed_login_count` / `locked_until` ; `mfa_recovery_code` — codes de
récupération hachés à usage unique §8-9 ; `mfa_challenge` — défi court entre « mot de passe OK » et
émission du jeton §8 ; RLS par utilisateur `user_id = current_user_id()` ; permission
`security.reset_mfa`, Phase 33), et `V047` le moteur de notification (`notification` += `category` /
`priority` / `pinned_at` / `archived_at` / `expires_at` / `dedup_key` / `source` ;
`notification_preference` — routage par catégorie & entreprise, priorité mini, heures calmes §5,
RLS par utilisateur ; `message_template` — gabarits versionnés par canal & langue, liste blanche de
variables, RLS système/entreprise §22-23 ; `notification_outbox` — file de remise des canaux
externes avec retry / backoff / dead-letter §28-31 ; permission `notifications.manage_templates`,
Phase 34), et `V048` les clés d'API de la plateforme ouverte (`api_key` — `key_prefix` visible +
`key_hash` SHA-256 [le secret n'est jamais persisté §14, §44] ; `scopes` jsonb bornés aux
permissions du propriétaire §13 ; `environment` test/live [secrets séparés §41] ; `expires_at`,
`rotated_from`, `revoked_at`, `rate_limit_per_minute` §13 ; `last_used_at`/`last_used_ip` ;
`enable_company_rls` + `trg_audit_api_key` ; permission `integrations.manage_api_keys`, Phase 35),
et `V049` le registre de continuité (`backup_policy` — RPO/RTO cibles, fréquence, rétention,
destination, chiffrement, immutabilité §4-7 ; `backup` — catalogue + `covers_up_to` point RPO +
`verified_at` / `verification_detail` + `expires_at` dérivé de la rétention §6, §21 ; `restore_run` —
tests & restaurations, `mode` test/production, `pending_approval` → double approbation §39 ;
`continuity_incident` — `severity` p1..p4, `category` 11 scénarios + ransomware, `runbook_step`
`CHECK 1..9` §43, `post_mortem` jsonb §45 ; `enable_company_rls` + `trg_audit_*` ; permissions
`continuity.{view,manage,restore}`, Phase 36),
et `V050` le registre de mise en service (`deployment` — table **plateforme** [pas de `company_id`,
pas de RLS, comme `schema_migration`] : `version`, `environment` local/test/staging/production,
`status` running/succeeded/failed/rolled_back, `schema_version` figé au déploiement,
`rolled_back_from` FK auto-référentielle + `rollback_reason` §34, §38-39 ; `golive_signoff` —
validation Go-Live par entreprise, `verdict` go/go_with_reservations/no_go, `checklist_snapshot`
jsonb **figé** à la signature, `forced` + `force_reason` [`CHECK NOT forced OR force_reason IS NOT
NULL`] §40, §54 ; `enable_company_rls` sur `golive_signoff`, `trg_audit_*` sur les deux ;
permissions `operations.{view,manage}`, Phase 37),
et `V051` la couche **plateforme SaaS** — 17 tables **sans RLS entreprise** (la facturation de
NEXORA est transverse aux locataires §2, §43-44 ; l'accès est filtré par les permissions
`platform.*` réservées à 7 rôles plateforme) : `platform_plan` / `platform_addon` / `platform_coupon`
(catalogue §4-6, §20, §40), `platform_customer` (`customer_ref` UNIQUE, `country_code`,
`billing_currency`, `tax_exempt`, `status`) + `platform_customer_company` (`UNIQUE(company_id)` —
une entreprise = un client §9), `subscription` (`status` trialing/active/past_due/grace/suspended/
canceled/expired, index partiel **un seul abonnement vivant par client**, `trial_ends_at`,
`current_period_*`, `grace_ends_at`, `payment_failures`) + `subscription_addon`, `platform_invoice`
(`number` UNIQUE via `platform_invoice_seq`, `kind` invoice/credit_note/receipt, `parent_invoice_id`)
+ `platform_invoice_line` (`line_type` plan/addon/proration/usage/discount/tax), `platform_payment`
(`customer_id` **nullable** — événement non rattaché §54, **`provider_event_id` UNIQUE** →
idempotence, `review_required`, `is_manual`) + `platform_payment_method` (jeton PSP uniquement,
`CHECK last4 ~ '^[0-9]{4}$'` §53), `usage_counter` (`UNIQUE(customer, resource, period_start)`,
`last_alert_pct` §21-23), `platform_license` (`scope` organization/company/user/terminal/module,
`grace_days`, `expires_at`, `token_hash` du jeton offline §27-28), `support_ticket` (`ticket_ref`
UNIQUE, `priority` p1..p4) + `support_ticket_message` (`is_internal`) + `support_access_grant`
(consentement `requested_by`, `expires_at`, `revoked_at` §38), `billing_event` (ledger d'événements
`TrialStarted`…`LicenseRevoked`, séparé des écritures §44) ; `trg_audit_*` sur les 14 tables
mutables ; 3 séquences ; **7 rôles** `platform_admin`/`billing`/`support`/`finance`/`security`/
`operator`/`readonly` + **11 permissions** `platform.*` ; `platform.portal` accordé à
`owner_direction` + `group_administrator`, Phase 38),
et `V052` la **migration & l'onboarding avancé** — 6 tables en RLS entreprise + audit :
`import_job` (`kind` customers/suppliers/products/opening_stock, `source_type` csv/json/xlsx/api/manual,
`content` fichier original + `content_hash` SHA-256, `status` 10 valeurs
`created→profiled→mapped→validated→simulated→executing→completed`, `mode` 5 valeurs
create_only/update_only/upsert/skip_duplicates/reject_duplicates §29, `batch_size` 1–10000,
compteurs, `profile`/`mapping`/`simulation`/`reconciliation` jsonb, `paused_at`, `finalized_at`),
`import_row` (`row_number` 1-based, `source` jsonb ligne brute, `dedup_key`, `match_type`
exact/probable/none §9, `status` 8 valeurs, `messages` jsonb `[{level,field,message}]` §26,
FK composite `ON DELETE CASCADE`, `UNIQUE (import_job_id, row_number)`), `import_record`
(`entity_type`/`entity_id`/`action` created|updated/`reverted_at` — trace pour rollback §37),
`import_mapping_template` (`UNIQUE (company_id, kind, name)`, mapping réutilisable §7),
`import_event` (ledger 13 événements §46), `onboarding_task` (`UNIQUE (company_id, task_key)`,
`status` pending/done/skipped — acquittements manuels de la checklist §32) ; `enable_company_rls`
+ `trg_audit_*` (5 tables mutables) ; permissions `migration.{view,manage}`, Phase 39),
et `V053` la **personnalisation UX** : `user_preference` += `high_contrast` (bool),
`text_scale` (`numeric(3,2)`, `CHECK 0.85–1.50` — l'échelle de texte ne peut jamais dégrader
la lisibilité §7, §35), `timezone` (§34), `shortcuts` jsonb ; `dashboard_layout` (`user_id NULL`
= défaut d'entreprise, `role` pour le ciblage §37, `widgets` jsonb `[{key,x,y,w,h,config}]`,
index uniques partiels d'un seul défaut par `(entreprise, utilisateur)` et par `(entreprise, rôle)`) ;
`saved_filter` (`view_key` « module.vue », `filter` jsonb, `shared` bool, `UNIQUE (user_id, view_key,
name)`, écriture réservée au propriétaire) ; `ui_shortcut` (`chord` unique par utilisateur, `action`,
`label`) ; RLS personnalisée + `trg_audit_*` sur les 3 tables ; **aucune nouvelle permission**
(libre-service ; le défaut d'entreprise passe par `admin.configure_company`), Phase 40),
et `V054` l'**administration opérationnelle & maintenance** — `job_definition`/`job_run` (registre
et historique des jobs planifiés, tables **plateforme** sans RLS comme `deployment` ; 9 jobs
seedés), `feature_flag` (bascules global/entreprise/utilisateur, `UNIQUE NULLS NOT DISTINCT (key,
scope, company_id, user_id)`, table plateforme), `maintenance_window` (état machine `planned →
notified → backed_up → in_progress → verifying → closed`/`canceled`, index unique **une seule
fenêtre `in_progress`**, table plateforme), `retention_policy` (4 types de données **techniques**
fermés — jamais métier ni comptable — défaut système `company_id NULL` + surcharge par entreprise,
RLS personnalisée), `purge_run` (journal aperçu/exécuté avec `protected_excluded_count`, même RLS),
`data_correction_request` (workflow de correction à 4 yeux, `CHECK (approved_by IS NULL OR
approved_by <> requested_by)`, même RLS) ; `trg_audit_*` sur les 7 tables ; permission
`operations.correct_data` (s'ajoute à `operations.{view,manage}` de la Phase 37), Phase 41),
et `V055` la **Business Intelligence, Reporting & Pilotage** — `kpi_definition` (table **plateforme**
sans RLS comme `job_definition` : catalogue KPI gouverné, une définition unique pour tout NEXORA
ONE, amorcée depuis le catalogue Phase 7), activation de 4 tables posées en Phase 7 (`kpi_target`
`+ weight`, `saved_report`, `report_schedule`, `report_execution`) jusque-là sans aucun service,
activation du CRUD sur `budget`/`budget_line` (Phase 7 : statut `+ pending_approval`, `+
revised_from`/`submitted_by`/`approved_by`/`approved_at`), `insight` (RLS entreprise, anomalies
déduplicées par `UNIQUE NULLS NOT DISTINCT (company_id, category, drill_path, period_start)`),
`forecast` (RLS entreprise, `points` jsonb, `confidence_level` jamais une certitude) ; **correction
de schéma** : la FK composite `report_execution_schedule_fk (report_schedule_id, company_id)
REFERENCES report_schedule(id, company_id) ON DELETE SET NULL` aurait mis `company_id` à `NULL`
(violant son `NOT NULL`) à la suppression d'un rapport planifié — jamais constaté avant cette phase
(aucun service ne consommait encore ces tables) — ramenée à une FK simple sur `report_schedule_id`
seul ; `trg_audit_*` sur `kpi_definition`/`insight`/`forecast`/`budget` ; permissions
`reporting.{manage_budgets,forecast,manage_insights}` (s'ajoutent à `reporting.{schedule,
manage_targets}` de la Phase 7, jusque-là accordées sans fonctionnalité derrière), Phase 42),
et `V056` l'**Intelligence métier & recommandations** — `recommendation` (RLS entreprise, colonne
**générée** `priority_score = impact_score * urgency_score * GREATEST(confidence_score,1)` §46,
dédup `UNIQUE NULLS NOT DISTINCT (company_id, category, drill_path, period_start)` comme `insight`,
cycle `new → accepted|dismissed|snoozed → resolved` avec `outcome` positif/négatif — boucle
d'apprentissage §33), `intelligence_rule` (`company_id` nullable = gabarit système comme
`sector_profile`, 5 règles amorcées), `entity_score` (RLS entreprise, `UNIQUE (company_id,
entity_type, entity_id, score_key)`, upsert à chaque recalcul), `assistant_query` (RLS entreprise,
journal d'ajout seul comme `report_execution` — non audité par trigger), `task` (RLS entreprise,
comble un manque réel : `group_task` exigeait un groupe, `onboarding_task` était une checklist
fixe) ; `trg_audit_*` sur `recommendation`/`intelligence_rule`/`entity_score`/`task` ; permissions
`intelligence.{view,manage_rules,act_on_recommendation,use_assistant}` ; job planifié
`intelligence.generate_recommendations` (par entreprise, calcule aussi les scores), Phase 43),
et `V057` le **Mobile, terrain & multi-appareils** — `attachment` (RLS entreprise, blob `bytea` +
`sha256_hash`, `kind` photo|signature, délibérément minimal — Phase 45 GED généralisera),
`delivery_route`/`delivery_stop` (RLS entreprise, **premières coordonnées géographiques du
schéma** `latitude`/`longitude numeric(9,6)`, capturées uniquement à la preuve de livraison —
jamais un suivi continu §24 ; `sales_order_id` nullable, composite FK, complétion via
`OrderService.CompleteOrderAsync`), `field_intervention` (RLS entreprise, `checklist`/`parts_used`
jsonb — comble un manque réel, `support_ticket` de la Phase 38 étant le support NEXORA↔abonné,
sans rapport), `crash_report` (RLS entreprise, journal d'ajout seul comme `assistant_query`) ;
`sync_device` (Phase 10, V017) étendue de `user_id` (mobile = personnel, poste POS = partagé,
NULL) et `push_token`/`push_provider` (le canal `'push'` était déjà autorisé par
`notification_outbox`/`message_template` depuis le V047, jamais implémenté) ; `trg_audit_*` sur
`delivery_route`/`delivery_stop`/`field_intervention` ; permissions `field_ops.{view,manage}`.
`V058` ajoute la **GED, documents, archivage & signature** — `document` (RLS entreprise,
`document_type` CHECK 18 valeurs, `category`/`tags` jsonb indexé GIN, cycle de statut `draft →
pending_review → approved → signed → archived → frozen`, `locked`/`legal_hold`/`retention_until`/
`deletion_requested_*` — délibérément distincte de `issued_document` (Phase 30, émis/numéroté) et
`attachment` (Phase 44, capture terrain minimale, l'annonçait déjà : « Phase 45 GED généralisera »)),
`document_version` (RLS entreprise, contenu `bytea` ≤ 25 Mo, jamais écrasée, `UNIQUE(document_id,
version_no)`, `sha256_hash` indexé pour la détection de doublon non bloquante §39, champs OCR),
`document_link` (RLS entreprise, liens multiples polymorphes vers un objet métier, `UNIQUE
(document_id, linked_type, linked_id)`), `document_share` (RLS entreprise, jeton unique, expiration
obligatoire, mot de passe optionnel haché, révocable — l'accès public par jeton contourne la RLS via
`tenant.BeginSystemScope()`, même motif que `DocumentsController.Verify` Phase 30, un appelant
anonyme n'ayant aucun contexte d'entreprise), `document_signature_request` (**a nécessité l'ajout
a posteriori** de `document_signature_request_company_id_uk UNIQUE (id, company_id)` — cible d'une
FK composite, doit déclarer sa propre unicité) + `document_signer` (RLS entreprise, `sequence_no`
strictement respecté, `proof` jsonb, signataire interne ou externe), `document_retention_policy`
(`company_id` nullable = gabarit système comme `sector_profile`, 6 politiques amorcées —
**volontairement une table sœur, jamais une extension** de `retention_policy` Phase 41, qui ferme
sa liste à 4 types strictement techniques) ; `trg_audit_*` sur les 7 tables ; permissions
`ged.{view,upload,manage,sign,manage_retention}` ; jobs planifiés `ged.apply_retention` et
`ged.expire_signatures` (par entreprise).
`V059` ajoute l'**API, intégrations & écosystème** — `marketplace_app` (table plateforme sans RLS
comme `platform_plan`, slug unique, 15 catégories, 3 niveaux de certification, cycle `draft →
pending_review → approved/rejected/suspended`, scopes demandés jsonb, modèle tarifaire — 3 apps
officielles amorcées), `app_installation` (RLS entreprise, `UNIQUE(company_id, marketplace_app_id)`
— une seule ligne par app installée, réinstaller met à jour plutôt que dupliquer, scopes **accordés**
toujours ⊆ scopes demandés contrôlé applicativement, `UNIQUE(id, company_id)` — cible d'une FK
composite), `api_key_daily_usage` (RLS entreprise, `UNIQUE(api_key_id, usage_date)`, alimenté par
le middleware d'authentification, jamais bloquant). `api_key` (Phase 35, V048) étendue de
`app_installation_id` + `api_key_company_id_uk UNIQUE(id, company_id)` **ajoutée a posteriori** —
même leçon que `document_signature_request` en V058, cible d'une FK composite doit déclarer sa
propre unicité ; `trg_audit_*` sur `marketplace_app`/`app_installation` ; permissions
`platform.manage_marketplace` (curation, réservée personnel plateforme) et
`integrations.manage_apps` (installation entreprise).
`V060` ajoute la **Sécurité, IAM & Conformité** — `security_finding` (table plateforme sans RLS,
registre de vulnérabilités/résultats SAST-DAST-SCA-pentest, SLA calculé par sévérité à la
déclaration), `security_certificate` (table plateforme, classification automatique par échéance,
rotation avec lignée `rotated_to`), `compliance_control` (table plateforme, 15 contrôles
auto-évalués amorcés reflétant l'état réel du code, y compris les manques — jamais une
certification externe), `emergency_access_grant` (RLS entreprise, break-glass §12 : motif ≥ 10
caractères et durée 1-8h en CHECK, `emergency_access_grant_self_decision_chk` interdit
l'auto-approbation **en base**, `underlying_assignment_id` trace la ligne `user_role` réellement
créée par `UserAssignmentService.GrantAsync` — **aucun second mécanisme d'octroi**, celui de la
Phase 22 est réutilisé intégralement). `continuity_incident` (Phase 36, ALTER) `+ domain`
(`availability`/`security`) + catégorie étendue à 6 valeurs de sécurité — **jamais un second
registre d'incidents**. `user_session` (Phase 11, ALTER) `+ device_type` `+ trusted` — **un
appareil est une session**, jamais une table séparée. `trg_audit_*` sur les 4 nouvelles tables ;
permissions `security.request_emergency_access` / `security.approve_emergency_access` ; job
planifié `security.check_certificates`.
`V061` ajoute l'**Intelligence & Business Decision Engine** (Phase 54) — **aucune table neuve** :
le cahier des charges redemandait un système déjà livré en Phase 43 (V056), seul le delta réel a
été comblé par ALTER sur `recommendation` (Phase 43) : `+ nature` (risk/opportunity) `+
priority_level` (colonne **générée**, comme `priority_score` — Postgres interdisant qu'une colonne
générée en référence une autre, la même formule impact×urgence×confiance y est réécrite plutôt que
référencée) `+ expires_at` `+ override_value` ; `status` CHECK étendu (`+ modified, + expired`) ;
`category` CHECK étendu à 4 nouvelles règles (`sales_spike`, `sales_decline`, `customer_inactive`,
`supplier_price_increase` — les 3 domaines ventes/clients/fournisseurs qu'`intelligence_rule`
documentait déjà en backlog explicite Phase 43). Nouvel index
`recommendation_priority_level_idx`. 4 nouvelles lignes système dans `intelligence_rule` (même
gabarit que les 5 existantes). Aucune nouvelle permission.
`V062` ajoute la **Résilience, Haute Disponibilité & Disaster Recovery** (Phase 48) — le cahier des
charges (62 sections) redemandait un registre déjà livré en Phase 36 (V049) et réclamait par
ailleurs une infrastructure HA/réplication/failover hors périmètre applicatif ; seul le delta réel
a été comblé. **1 table neuve** : `dr_test` (RLS entreprise — registre d'exercices DR distinct de
`restore_run`, qui exige toujours un `backup_id` concret ; `test_type` fermé à 10 valeurs
tabletop/restore/failover_db/failover_app/network_outage/server_outage/storage_outage/
ransomware_simulation/site_unavailable/return_to_normal ; `status`
scheduled/passed/failed/cancelled ; un exercice ne se consigne qu'une fois). `continuity_incident`
(Phase 36, ALTER) `+ containment_actions` (jsonb, défaut `[]`) — confinements **réellement
exécutés** (ex. verrouillage d'urgence révoquant sessions et clés d'API via `security_epoch`),
jamais seulement une intention documentée dans le post-mortem. `backup` (Phase 36, ALTER)
`backup_kind_chk` étendu (`+ differential, + metadata, + keys`). `trg_audit_dr_test` ; aucune
nouvelle permission (réutilise `continuity.{view,manage,restore}` — un exercice DR se gère comme
un test de restauration, un confinement comme une restauration production).
`V063` ajoute les corrections d'authentification de la **recette Phase 55** — `user_session`
(ALTER) `+ token_id` (uuid, unique partiel) : chaque jeton porte un `jti` enregistré sur sa session,
et le middleware refuse un jeton dont la session est révoquée (déconnexion réelle ; révoquer une
session depuis l'écran Sessions coupe désormais aussi son jeton). **1 table neuve** :
`password_reset` (RLS par utilisateur comme `mfa_challenge` ; code de réinitialisation stocké haché
SHA-256, à usage unique, expiration 30 min ; le parcours anonyme « mot de passe oublié » passe
explicitement en portée système pour ces seules lignes). Aucune nouvelle permission.
`V064`–`V072` : guide fonctionnel développeur et résumé fonctionnel (rôles et permissions POS,
demandes de retour et de remise validées par un Manager, sessions de caisse, visibilité des ventes,
isolation par succursale `branch_isolation`, fonction `bootstrap_company_defaults` de l'assistant
d'installation) — détail dans [`docs/guide-fonctionnel-conformite.md`](../docs/guide-fonctionnel-conformite.md).
`V073` ajoute le module **Gestion clients & comptes clients**
([`docs/module-clients-comptes-clients-implementation.md`](../docs/module-clients-comptes-clients-implementation.md)) —
`customer` (ALTER) : prénom, ville, pays, contact secondaire, segment, responsable, durée et
conditions de crédit, succursale, notes, créé/modifié par ; `customer_receivable` (ALTER) :
`credit_number` unique `CRD-AAAA-NNNNNN`, `source_type`/`source_id` (vente / dépense), auteur,
succursale, annulation motivée — valeurs posées par un trigger `BEFORE INSERT` ; `payment` (ALTER) :
`note`, `voided_at/by`, `void_reason`, `void_journal_entry_id` ; `payment_allocation` (ALTER) :
`voided_at/by` — une affectation annulée n'est jamais supprimée ni réactivée
(`trg_payment_allocation_guard`) et `payment_allocation_apply` ne compte plus que les affectations
actives via `recompute_customer_receivable()` (versements + avoirs imputés + radiations). **6 tables
neuves**, RLS entreprise : `customer_expense` (4-yeux en base, suppression interdite une fois
validée), `customer_credit_memo` (avoir, `remaining_amount` généré, `gl_account_key`),
`customer_credit_application` (utilisation sur un crédit ou par un remboursement),
`customer_refund` (4-yeux en base), `collection_action`, **`customer_ledger_entry`** — journal
financier client **immuable**, alimenté par triggers sur crédit, affectation, radiation, avoir et
remboursement ; solde = Σ débit − Σ crédit = crédits dus − avoirs disponibles ; historique existant
repris. Permissions `credit.{receive_payment,void_payment,refund}`,
`customers.{create_expense,approve_expense}` ; réglages `credit.{overpayment_policy,
default_due_days,near_limit_percent}` ; job `credit.scan_alerts`.
`V074` corrige les écarts du module clients — **2 tables neuves** (RLS entreprise, audit) :
`credit_schedule` (échéancier versionné : `paid_before`, `total_amount`, une seule version active)
et `credit_installment` (lignes immuables) ; `recompute_customer_receivable()` fait de la première
échéance non couverte l'échéance du crédit. `approval_request` += `context` jsonb, `consumed_at`,
`consumed_by_id` (dépassement de crédit du POS par le workflow `credit_sale`, consommé par une seule
vente). `nexora.convert_amount(montant, de, vers, date)` : conversion au dernier taux (direct puis
inverse), NULL si aucun taux. `offline_policy` += `allow_offline_customer_payment`.
`customer_credit_application` += `operation_uuid` (idempotence). `customer_credit_memo.gl_account_key`
+= `accounts_receivable` ; reprise des retours antérieurs à V073 en avoirs `AVC-REPRISE-…` imputés.
Permission `credit.create` (crédit hors vente).

`V075` ajoute le module **Paie, RH & avances sur salaire** — **25 tables** en RLS entreprise + audit : `department`, `employee` (identifiants bancaire et fiscal chiffrés + 4 derniers chiffres), `employee_contract` (EXCLUDE : pas deux contrats actifs qui se chevauchent), `salary_history` (immuable, `previous_amount` posé par trigger), `pay_component`, `employee_pay_element`, `payroll_period` (EXCLUDE : pas deux périodes régulières qui se chevauchent), `payroll_run` (garde : aucun retour arrière après approbation), `payroll_item` (CHECK net = brut + remboursements − retenues), `payroll_earning`, `payroll_deduction`, `attendance_record`, `leave_record`, `overtime_record`, `payroll_variable`, `payroll_rule` (figée une fois validée), `employee_advance` (solde généré), `advance_installment`, `advance_repayment` (immuable ; trigger refusant tout remboursement supérieur au solde), `employee_advance_event`, `employee_expense_claim`, `salary_payment` (un seul paiement actif par bulletin), `payslip` (figé), `payroll_adjustment`, `employee_document`. `payroll_time_guard` verrouille présences, congés, heures supplémentaires et variables inclus dans une paie validée et marque une paie calculée comme périmée.
`V076` : 24 permissions `hr.*` / `payroll.*`, rôles `hr_manager` et `employee`, réglages `payroll.*`, clés et comptes de paie + journal `PA` (`bootstrap_payroll_defaults`, branché sur l'amorçage d'entreprise), composants de paie, modèles de règles haïtiennes (statut `template`), types d'approbation, modèle de notification, job `payroll.scan_alerts`.
`V077` : `bootstrap_payroll_group_mapping` — comptes de paie rattachés au plan de groupe (`GA-PERS`, `GA-PERSREC`, `GA-PERSEXP`) pour la consolidation.
`V078` (Production Readiness REQ-03) : `user_session` += `refresh_token_hash`, `refresh_expires_at`, `refresh_used_at` + index — aucune nouvelle table.
Le journal d'audit
`audit_log` est **immuable** (trigger `V030`). L'API
(rôle `nexora_app`, sans `BYPASSRLS`) **doit** positionner le contexte par transaction :

```sql
SET LOCAL nexora.company_ids = '<uuid>[,<uuid>...]';  -- périmètre entreprise
SET LOCAL nexora.group_ids   = '<uuid>[,<uuid>...]';  -- périmètre groupe (consolidation)
SET LOCAL nexora.user_id     = '<uuid>';              -- auteur (audit, notifications)
```

Tâches système / imports / migrations : `SET LOCAL nexora.bypass_rls = 'on'` (rôle habilité
uniquement). Les migrations tournent en superutilisateur (bypass implicite).

Drapeaux d'exception contrôlés :

| Drapeau | Effet |
|---|---|
| `nexora.allow_closed_period = 'on'` | autorise une écriture d'ajustement dans une période clôturée |
| `nexora.allow_hard_delete = 'on'`   | autorise la suppression physique d'une transaction validée (maintenance) |

## Règles d'intégrité appliquées par le moteur (`V019`)

- **Débit total = crédit total** pour toute écriture `posted` (contrainte différée) ; ≥ 2 lignes.
- Écritures uniquement sur **comptes postables**.
- **Périodes clôturées** protégées ; `entry_date` obligatoirement dans la période.
- **Écriture validée immuable** — correction par extourne (`reversal_of_entry_id`).
- **Affectation de paiement** ≤ montant du paiement et ≤ solde de la créance/dette ;
  mise à jour automatique de `paid_amount` / `status` ; devises cohérentes.
- `sale.amount_paid` maintenu depuis `sale_payment` ; `stock_balance` maintenu depuis
  `stock_movement` (coût moyen pondéré, `lot_stock` si lot).
- **Pas de suppression physique** d'une vente / paiement / facture / réception / écriture validée.
- **Journal d'audit** automatique sur 15 tables sensibles (`audit_log`).

## Numérotation des documents

```sql
SELECT nexora.next_document_number(:company_id, 'sale', :establishment_id, 2026);
-- -> 'VTE2026-000001'   (préfixe / année / compteur configurables par document_sequence)
```

## Test

`smoke_test.sql` monte un contexte complet et vérifie les scénarios §32
(vente comptant, vente à crédit + règlement partiel, achat à crédit + règlement,
écriture équilibrée) puis les rejets attendus §31 (déséquilibre, compte non postable,
période clôturée, sur-affectation, suppression interdite) et l'isolation RLS.
Tout se déroule dans une transaction **annulée** : aucune donnée résiduelle.
