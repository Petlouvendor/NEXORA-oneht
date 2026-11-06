# 06 — Audit de la base de données (édition 2)

> PostgreSQL 18 local. Mesures par requêtes **SELECT** (session en `default_transaction_read_only`) sur le catalogue et
> les données de `nexora_recette`, les 2026-10-16 et 2026-10-17, HEAD `6a47c6d`. Aucune migration lancée, aucune
> écriture dans `nexora` ni `nexora_recette`. Édition 1 (2026-10-13) : historique Git.

## 1. Schéma

| Élément | Valeur | Évolution depuis l'éd. 1 |
| --- | --- | --- |
| Migrations | 102 fichiers (`V001`…`V102`) + `.gitkeep` ; 102 lignes dans `schema_migration` | + V100 (A-018), V101 (A-029/A-030), V102 (A-011) |
| Rejeu sur base vierge | par l'assistant d'installation dans `SetupWizardTests` (suite d'API verte) | nouveau constat |
| Tables du schéma `nexora` | 291 | = |
| Tables avec RLS activée **et forcée** | 251 | +1 (`role_permission`) |
| Tables sans RLS | 40 | −1 |
| Fonctions `SECURITY DEFINER` | 5, toutes avec `search_path = nexora, pg_temp` | vérifié |
| Rôle applicatif `nexora_app` | ni superutilisateur, ni BYPASSRLS ; INSERT/UPDATE/DELETE retirés sur `schema_migration`, UPDATE/DELETE sur `audit_log` | A-030 corrigé |

## 2. Isolation

- L'intercepteur de commande réapplique le contexte RLS après un changement de portée système (A-009 corrigé dans le code).
- Politiques V101 : rôles système lisibles par tous, modifiables seulement par la portée système ; `role_permission`
  sous RLS ; `audit_log` n'accepte une insertion que pour une entreprise du contexte (ou aucune).
- **Résidu A-009** : `app_user`, `user_role` et `access_scope` restent sans RLS, avec tous les droits DML accordés à
  `nexora_app` ; leur isolation repose sur le code. Les autres tables sans RLS sont des tables de plateforme ou de
  référence (choix du projet) : `feature_flag`, `platform_customer_company`, `support_access_grant`, `support_ticket`
  portent une colonne `company_id` sans RLS.

## 3. Contrôles d'intégrité sur les données (`nexora_recette`)

| Contrôle | Résultat |
| --- | --- |
| Stock : solde = Σ mouvements (Pharmacie) | 140 285,00 = 451 750 − 305 830 − 6 000 + 365 ✔ |
| Compte de stock du grand livre (Pharmacie) | 61 696,20 : écart −78 588,80 avec la valorisation |
| Origine de l'écart | 2 ventes en USD : coût des ventes comptabilisé 40 500 au lieu de 300 chacune (−80 400, **A-052**) ; écarts de prix factures / réceptions capitalisés au compte de stock (+1 811,20, FAIBLE) |
| Supermarché : stock 673 556,60 / grand livre 682 647,79 | écart de 9 091,19 non décomposé (aucune vente en devise dans cette entreprise ; probablement écarts de prix factures / réceptions — à analyser) |
| Écritures `reversed` | 0 : A-043 invisible dans ces données |
| Exercices comptables | « Exercice 2026 » seulement, pour les 3 entreprises (**A-042**) |
| Comptes financiers en devise | Pharmacie : banque en USD (74 890,15 USD) additionnée aux HTG par les KPI (**A-046**) |
| Ventes en devise | 2 (USD) |
| Avoirs fournisseurs | 0 : résidu A-022 invisible |
| Gabarits de messages HTML | 0 |
| Droits des rôles stock / achats | `intelligence.view`, `reporting.view`, `reporting.export`, sans permission financière (**A-048**) |

## 4. Problèmes et risques

| ID | Gravité | Constat |
| --- | --- | --- |
| A-052 | CRITIQUE | Écritures de coût des ventes en devise multipliées par le taux (données réelles) |
| A-042 | ÉLEVÉ | Aucune période au-delà du 2026-12-31 ; seule l'amorçage (V072) en crée une |
| A-043 | CRITIQUE | Agrégats comptables sur `status = 'posted'` seul (12 fichiers, 23 requêtes) alors que l'origine extournée passe à `reversed` |
| A-009 (résidu) | MOYEN | Tables d'identité sans RLS |
| A-050 | MOYEN | Stock d'ouverture importé sans écriture |
| Obs. | FAIBLE | Contrôle « écritures déséquilibrées » de `restore.sh` en devise de saisie (faux échec possible sur une écriture multidevise légitime) |

## 5. Non vérifié

Relecture ligne à ligne des migrations V001-V099 (inchangées depuis l'éd. 1 ; contrôlées par le catalogue et le rejeu
sur base vierge) ; volumétrie et plans d'exécution sous charge ; restauration réelle d'une sauvegarde par l'audit ;
décomposition complète de l'écart de 9 091,19 du Supermarché.
