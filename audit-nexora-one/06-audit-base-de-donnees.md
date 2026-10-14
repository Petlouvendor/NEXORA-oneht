# 06 — Audit de la base de données

> PostgreSQL 18 local (service `postgresql-x64-18`). Toutes les mesures ci-dessous sont des requêtes **SELECT** sur le
> catalogue ou les données, exécutées le 2026-10-13 sur `nexora_recette` (jeu de recette, 88 Mo) et `nexora` (base de
> développement, modèle des tests d'intégration). Aucune migration, aucune écriture.

## 1. Schéma

| Élément | Valeur | Source |
|---|---|---|
| Migrations | 99 fichiers `database/migrations/V001…V099`, 99 lignes dans `nexora.schema_migration` (dernière V099) | `ls`, SELECT |
| Migrations embarquées dans l'API (assistant d'installation, `migrate`) | oui ; CI : `migrate --status` doit répondre 0 | `.github/workflows/ci.yml:71-75` |
| Tables du schéma `nexora` | 291 | `pg_tables` |
| Propriétaire des tables | `postgres` (dev) ; `nexora_owner` en production (compose) | `pg_tables`, `docker-compose.prod.yml:32` |
| Rôle applicatif | `nexora_app` : ni superutilisateur, ni BYPASSRLS | `pg_roles` |
| Tables avec RLS activée **et forcée** | 250 | `pg_class.relrowsecurity/relforcerowsecurity` |
| Tables sans RLS | 41 (liste §3) | idem |
| Politiques RLS | 275 ; 227 s'appuient sur `rls_allows_company()`, 11 sur `rls_allows_group()` | `pg_policies` |
| Clés étrangères | 890 | `pg_constraint` |
| Contraintes CHECK | 641 | idem |
| Contraintes UNIQUE | 277 | idem |
| Index | 1 028 (dont 45 ajoutés sur les clés étrangères par V099) | `pg_indexes` |
| Déclencheurs (hors internes) | 476 | `pg_trigger` |
| Fonctions | 79 | `pg_proc` |
| Vues | 0 | `pg_views` |

Les relations principales (vente → lignes → mouvements de stock → écritures ; créances → affectations de paiement ;
compte financier → transactions ; employé → contrats → bulletins) sont portées par des clés étrangères ; les
invariants financiers par des CHECK et des déclencheurs (journal client append-only V073-V074, `audit_log` immuable,
soldes de stock et coût moyen tenus par déclencheurs, garde de session de caisse close).

## 2. Mécanisme d'isolation

- Fonctions : `current_company_ids()` lit `nexora.company_ids` (liste vide si absente ou illisible), `rls_bypass()`
  lit `nexora.bypass_rls`, `rls_allows_company(target)` = `rls_bypass() OR target IS NULL OR target = ANY(current_company_ids())`.
- Les variables sont posées par l'application à l'ouverture de connexion (`RlsConnectionInterceptor`) : voir A-009.
- `nexora.bypass_rls` est une variable de session que le rôle applicatif peut positionner lui-même : la RLS protège
  contre l'oubli d'un filtre dans le code, pas contre une injection SQL (aucune injection n'a été trouvée : tout le SQL
  brut passe par `FormattableString` paramétré).
- `target IS NULL` rend visibles à toutes les entreprises les lignes « modèles système » (21 tables RLS ont un
  `company_id` nullable : `role`, `setting`, `message_template`, `sector_profile`, `payroll_rule`, `pay_component`…).

## 3. Tables sans RLS (41)

`access_scope`, `app_user`, `auth_refresh_token`, `billing_event`, `compliance_control`, `currency`, `deployment`,
`exchange_rate`, `feature_flag`, `fraud_rule`, `job_definition`, `job_run`, `kpi_definition`, `maintenance_window`,
`marketplace_app`, `notification_channel_test`, `permission`, `platform_addon`, `platform_coupon`, `platform_customer`,
`platform_customer_company`, `platform_invoice`, `platform_invoice_line`, `platform_license`, `platform_payment`,
`platform_payment_method`, `platform_plan`, `role_permission`, `schema_migration`, `sector_module_preset`,
`security_certificate`, `security_finding`, `setting_definition`, `step_up_failure`, `subscription`,
`subscription_addon`, `support_access_grant`, `support_ticket`, `support_ticket_message`, `usage_counter`, `user_role`.

Lecture : la plupart sont des tables de plateforme ou de référence (choix assumé du projet). Les tables d'identité
`app_user`, `user_role`, `access_scope` et `role_permission` relèvent d'une isolation **par le code seulement** (A-009).
`exchange_rate` est globale (A-017).

## 4. Contrôles d'intégrité sur les données (SELECT)

| Contrôle | nexora_recette | nexora |
|---|---|---|
| Écritures validées déséquilibrées | 0 (sur 2 580) | 0 |
| Solde de stock ≠ Σ mouvements | 0 (sur 5 836 mouvements) | 0 |
| `current_balance` ≠ ouverture + Σ transactions | 0 | 0 |
| Créances : payé < Σ affectations | 0 | 0 |
| Sessions closes avec écart | 1 sur 3 (−150) | 0 |
| Retours espèces rattachés à une session | 0 sur 4 | — |
| Trésorerie ↔ grand livre | écart = solde d'ouverture de chaque caisse (60 000 ; 45 000) | 45 000 |
| Taux de change | 2 lignes (démo : USD→HTG 132 puis 135) | 2 |
| Rôles globaux (`company_id NULL`) non système | 0 (22 rôles système) | 0 |

Interprétation : sur des données générées séquentiellement, le moteur reste équilibré et le stock cohérent. Les défauts
démontrés à l'exécution (A-002, A-018) n'apparaissent pas dans ces jeux, car ils supposent des retours espèces en session
ou des opérations concurrentes, absents de la recette. Leur absence ici **ne prouve pas** leur absence en exploitation.

## 5. Problèmes et risques

| ID | Gravité | Constat | Preuve |
|---|---|---|---|
| A-018 | CRITIQUE | `financial_account.current_balance` : aucun déclencheur ni jeton de concurrence ; seul écrivain = `AdjustBalanceAsync` en lecture-modification-écriture | grep migrations + EXEC |
| A-002 | CRITIQUE | Type `sale_refund` de `cash_movement_type_chk` (V011:130-131) jamais écrit | grep dépôt |
| A-031 | CRITIQUE (déploiement partagé) | `maintenance_window`, `job_definition`, `deployment` sans `company_id` ni RLS ; `data_correction_request` sous RLS mais lue et modifiée en portée système par `SqlDataCorrectionService` | catalogue + lecture |
| A-017 | ÉLEVÉ | `exchange_rate` globale, sans `company_id`, jamais écrite par l'application | V002:23-37, grep |
| A-009 | MOYEN | `bypass_rls` positionnable par `nexora_app` ; tables d'identité sans RLS | `pg_roles`, `pg_class` |
| A-029 | FAIBLE | Politiques qui autorisent aussi l'**écriture** de lignes globales : `role_isolation` (`WITH CHECK … OR company_id IS NULL`) ; `role_permission` sans RLS ; `audit_isolation` avec `WITH CHECK true` (insertion pour n'importe quelle entreprise). Aujourd'hui, le code refuse toute modification d'un rôle système (`SettingsAndRoles.cs:271,282,320,339`) et aucun rôle global non système n'existe : défaut de défense en profondeur seulement | `pg_policies`, lecture |
| A-030 | FAIBLE | `nexora_app` a `INSERT` sur `schema_migration` et `UPDATE/DELETE` sur `audit_log` (bloqués par `trg_audit_log_immutable`) : privilèges plus larges que nécessaire | `has_table_privilege` |
| A-023 | MOYEN | `opening_balance` des comptes financiers sans écriture de contrepartie | Données |
| Obs. | — | Base `nexora` (modèle des tests) polluée par les tests d'API à chaque exécution (T-002) | incident d'audit |

## 6. Non vérifié

- Relecture ligne à ligne des 99 migrations (contrôlées globalement par le catalogue, pas fichier par fichier).
- Rejeu complet des migrations sur une base vide (l'audit n'a créé qu'un clone `nexora_audit_api` par `CREATE DATABASE … TEMPLATE`).
- Volumétrie et plans d'exécution sous charge (aucun test de charge).
- Restauration d'une sauvegarde (outillée par le projet, non rejouée par l'audit).
