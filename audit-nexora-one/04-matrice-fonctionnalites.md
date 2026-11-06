# 04 — Matrice de vérité des fonctionnalités (édition 2)

> Édition 2 du 2026-10-16/17, HEAD `6a47c6d` (édition 1 : HEAD `e5e243d`, commit d'audit `ee4f025`). Une cellule « ✔ »
> signifie que l'élément **existe et est relié** (code lu ou route appelée), pas qu'il fonctionne. La colonne « Tests
> exécutés » ne cite que des tests réellement lancés pendant l'audit.
> Abréviations : **UNIT** = tests unitaires (103/103) ; **INT** = tests d'intégration (532/532, majoritairement en
> superutilisateur, RLS exercée par quelques tests ciblés — T-001) ; **API** = tests d'API (200/200, base `nexora_api_it`) ;
> **VIT** = Vitest (279/279) ; **EXEC** = scénario exécuté par l'audit sur l'API locale de HEAD et une base jetable clonée
> de `nexora_recette` ; **DONNÉES** = SELECT en lecture seule sur `nexora_recette` ; **E2E** = Playwright, **non exécuté**.

## 1. Matrice générale

| Module | Fonctionnalité | Frontend | Backend | Base de données | Tests exécutés | Verdict éd. 2 |
|---|---|---|---|---|---|---|
| Authentification | Connexion mot de passe, verrouillage | ✔ | ✔ `LoginHandler` (hachage factice) | `app_user` | UNIT, API, EXEC (6 connexions) | **FONCTIONNEL VÉRIFIÉ** ; compte verrouillé identifiable (A-011 résidu) |
| Authentification | Jeton 15 min + rafraîchissement HttpOnly, rotation | ✔ `apiClient` | ✔ | `auth_refresh_token` | API SessionSecurityEndpointsTests | **FONCTIONNEL VÉRIFIÉ** (tests verts) |
| Authentification | MFA TOTP, anti-rejeu, codes de secours | ✔ | ✔ (V102) | `mfa_*`, `mfa_last_step` | INT MfaAuthenticationTests | **FONCTIONNEL VÉRIFIÉ** |
| Authentification | Confirmation forte (step-up) | ✔ | ✔ 41 actions | `step_up_challenge` | EXEC (extourne A-043) | **FONCTIONNEL VÉRIFIÉ** ; absente sur le versement d'avance (FAIBLE) |
| Entreprises | Installation / amorçage | ✔ `/setup` | ✔ `SetupInstaller` (owner_direction) | migrations embarquées | API SetupWizardTests (base vierge) | **FONCTIONNEL VÉRIFIÉ** ; une seule période créée (A-042) |
| Entreprises | Isolation multi-entreprise | — | ✔ middleware + RLS + intercepteur de commande | RLS forcée 251/291 | API MultiTenantIsolationTests ; INT RLS ciblés | **FONCTIONNEL VÉRIFIÉ** au niveau requête ; tables d'identité sans RLS (A-009 résidu) ; motif A-034 |
| Utilisateurs | Rôles, affectations, garde anti-élévation | ✔ | ✔ | `role`, `user_role` | INT IdentityAccessTests | PARTIELLEMENT IMPLÉMENTÉ — périmètre de `manage_users` trop large (A-033) |
| Produits | Catalogue, unités, codes-barres | ✔ | ✔ | `product*` | INT Catalog*Tests | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| Produits | Listes de prix (A-013 corrigé) | ✔ | ✔ appliquées en vente | `price_list` | INT | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| Produits | Promotions, coupons, simulateur | ✔ | ✔ | `promotion`, `coupon` | EXEC (A-039) | **DÉFECTUEUX** — simulateur ≠ caisse (A-039) |
| Stock | Mouvements, soldes, coût moyen | ✔ | ✔ `IStockLedger` | triggers | INT ; DONNÉES (stock = Σ mouvements) | **FONCTIONNEL VÉRIFIÉ** |
| Stock | Inventaire physique (A-007 corrigé) | ✔ | ✔ | `inventory_count*` | INT | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ ; validation concurrente non protégée (A-038) |
| Stock | Transferts | ✔ | ✔ | `stock_transfer*` | EXEC | **DÉFECTUEUX** — 1 unité expédiée, 8 reçues en concurrence (A-038) |
| Stock | Stock d'ouverture (import) | ✔ | ✔ | `stock_movement` opening | — | PARTIELLEMENT IMPLÉMENTÉ — sans écriture comptable (A-050) |
| POS | Vente (cas nominal) | ✔ | ✔ `CheckoutAsync` atomique | `sale*` | INT ; EXEC (VTE2026-000444) | **FONCTIONNEL VÉRIFIÉ** pour le prix ; taxe choisie par le client (A-040) |
| POS | Vente en devise | ✔ | ✔ | écritures | DONNÉES | **DÉFECTUEUX** — coût des ventes × taux (A-052), arrondis (A-041) |
| POS | Retour / remboursement (A-002, A-005 corrigés) | ✔ | ✔ | `sale_return`, `cash_movement` | INT | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ à l'exécution |
| POS | Session de caisse (A-019 corrigé) | ✔ | ✔ | `cash_session` | INT | IMPLÉMENTÉ ; session non exigée par défaut (`pos.require_cash_session = false`) |
| POS | Caisse hors ligne / synchro | ✔ SW, file chiffrée | ✔ | `sync_*` | INT ; VIT ; EXEC | **DÉFECTUEUX** — vente ajoutée à une session close par `/sync` (A-003 résidu) |
| Ventes | Devis → commande → acompte → facture | ✔ | ✔ | `quote`, `sales_order`, `issued_document` | INT | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| Clients | Compte client, versements, avoirs | ✔ | ✔ | `customer_*` | INT | **FONCTIONNEL VÉRIFIÉ** (tests) ; annulation de versement → A-043 sur les états |
| Crédit | Recouvrement, promesses, litiges | ✔ | ✔ | `credit_*` | INT | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| Achats | Commande, réception (4081), facture, 3 voies | ✔ | ✔ | `purchase_*` | INT | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ ; écarts de prix non revalorisés en stock (FAIBLE) |
| Fournisseurs | Dettes, avoirs, paiements | ✔ | ✔ | `supplier_payable` | INT | IMPLÉMENTÉ ; balance âgée du pilotage sans avoirs (A-022 résidu) |
| Finance | Comptes, transferts, solde d'ouverture, dépenses à 4 yeux | ✔ | ✔ | `financial_*` | INT ; EXEC (A-045) | **DÉFECTUEUX** — 4 yeux contournable par `/finance/expenses` (A-045) |
| Finance | Taux de change (A-017 corrigé) | ✔ | ✔ saisie datée | `exchange_rate` | EXEC (taux saisi) | **FONCTIONNEL VÉRIFIÉ** |
| Finance | Rapprochement bancaire | ✔ | ✔ | `bank_statement*` | INT | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ |
| Comptabilité | Moteur en partie double | — | ✔ | `journal_entry*` | INT ; UNIT ; DONNÉES | **DÉFECTUEUX** en devise (A-041, A-052) ; juste en devise de l'entreprise |
| Comptabilité | Écritures manuelles | ✔ | ✔ | | INT ; EXEC | **FONCTIONNEL VÉRIFIÉ** en HTG ; refus en devise selon montants (A-041) |
| Comptabilité | Extourne | ✔ | ✔ | | EXEC | **DÉFECTUEUX** — états faux après extourne (A-043) |
| Comptabilité | Périodes et exercices | ✔ | ✔ clôture, réouverture | `accounting_period` | INT ; DONNÉES | **DÉFECTUEUX** — pas de création d'exercice (A-042) ; réouverture d'exercice clos (A-044) |
| Comptabilité | Clôture d'exercice, à-nouveaux (A-020, A-021 corrigés) | ✔ | ✔ | | INT (2 exercices) | IMPLÉMENTÉ ; faussés par A-043 après extourne |
| Comptabilité | États financiers | ✔ | ✔ | | EXEC (balance) | **DÉFECTUEUX** (A-043) |
| RH / paie | Employés, contrats, temps, notes de frais | ✔ | ✔ | 25 tables | INT ; API | **FONCTIONNEL VÉRIFIÉ** (tests) |
| RH / paie | Calcul, validation, paiement, bulletins, avances | ✔ | ✔ | | UNIT ; INT ; API | **FONCTIONNEL VÉRIFIÉ** (tests) ; annulation de paiement → A-043 |
| Pilotage | Centre de pilotage, KPI | ✔ | ✔ | | INT ; DONNÉES | **DÉFECTUEUX** en multidevise (A-046) |
| Pilotage | Rapports, export, rapports planifiés | ✔ | ✔ | | INT | PARTIELLEMENT IMPLÉMENTÉ — rapport planifié annoncé mais jamais livré (FAIBLE) |
| Pilotage | Budget vs réalisé | ✔ | ✔ | `budget*` | INT | **DÉFECTUEUX** (A-047) |
| Pilotage | Couche de confiance (rapprochement KPI) | ✔ | ✔ | `analytics_snapshot` | INT | IMPLÉMENTÉ ; hérite de A-043/A-046 |
| Intelligence | Recommandations, scores, assistant à règles | ✔ | ✔ déterministe | `recommendation*` | INT ; EXEC | **DÉFECTUEUX** sur la confidentialité (A-048) |
| Paiements | Simulation (déclarée) / MonCash / carte | ✔ | ✔ | `payment_intent` | INT | SIMULÉ (déclaré) / NON VÉRIFIABLE / ABSENT (déclaré) |
| Notifications | SMTP, Twilio, FCM | ✔ | ✔ « non configuré » honnête | `notification_outbox` | INT | **NON VÉRIFIABLE** sans comptes fournisseurs |
| Intégrations | Webhooks (HMAC, SSRF), clés d'API, marketplace | ✔ | ✔ | | UNIT ; INT ; API | **FONCTIONNEL VÉRIFIÉ** (tests) |
| GED | Documents, versions, partage, signatures | ✔ | ✔ | `document*` | INT ; API | **FONCTIONNEL VÉRIFIÉ** (tests) ; OCR absent (déclaré) |
| Groupe | Consolidation, intragroupe | ✔ | ✔ | | INT ; API | IMPLÉMENTÉ ; faussé par A-043 ; bilan consolidé sur la période seulement |
| Restaurant / terrain | Salle, cuisine ; tournées, preuves | ✔ | ✔ | | INT | IMPLÉMENTÉ, MAIS NON VÉRIFIÉ par l'audit |
| Continuité | Sauvegardes, restaurations, incidents | ✔ | ✔ registre | `backup`, `restore_run` | INT | **SIMULÉ** côté contrôle (A-051) ; scripts réels `backup.sh`/`restore.sh` |
| Exploitation | Console `/ops/*` (A-031 corrigé) | ✔ | ✔ garde plateforme | | INT ; API | **FONCTIONNEL VÉRIFIÉ** (lecture) |
| Administration | Versions de configuration (publier, revenir) | ✔ | ✔ statut seulement | `config_version` | INT | **PRÉSENT MAIS NON CONNECTÉ** — retour arrière sans effet (A-037) |
| Plateforme SaaS | Abonnements, quotas, licences, facturation | partiel | ✔ | 17 tables sans RLS | INT ; API ; EXEC | **DÉFECTUEUX** (A-049) ; quotas et statut d'abonnement sans effet sur l'usage |
| Mobile natif | Application | ❌ (web mobile) | API | | — | **ABSENT** (déclaré) |

## 2. État de chaque module

### Comptabilité et finance
1. Existant : moteur en partie double, écritures manuelles, plan comptable, périodes, clôtures, extournes, trésorerie,
   dépenses, rapprochement, taux de change.
2. Vérifié : équilibre en devise de l'entreprise ; saisie des taux ; écriture manuelle HTG ; extourne (exécutées).
3. Défectueux : états après extourne (A-043), coût des ventes en devise (A-052), arrondis en devise (A-041), absence de
   création d'exercice (A-042), réouverture d'exercice (A-044), routes brutes (A-045).
4. Risque principal : arrêt complet des écritures au 2027-01-01 (A-042).
5. Tests manquants : extourne puis états ; vente en USD à taux non entier ; vente au 1er jour d'un exercice non créé.

### Caisse et stock
1. Vérifié : vente nominale, prix serveur, coût moyen, cohérence stock = Σ mouvements.
2. Défectueux : taxe client (A-040), réceptions concurrentes (A-038), `/sync` en session close (A-003 résidu),
   simulateur (A-039).
3. Tests manquants : concurrence sur réception/annulation/validation ; `taxId` client ; rejeu dans une session close.

### Sécurité et autorisations
1. Vérifié : session, step-up, isolation par requête, garde plateforme (A-031), politiques V101.
2. Défectueux : A-033, A-034, A-045, A-048, A-049 ; résidus A-009, A-011.
3. Tests manquants : responsable stock sur `/intelligence/*` ; comptable sur `/finance/*` ; webhook avec le jeton par défaut.

### Pilotage et intelligence
1. Vérifié : visibilité des KPI par permission (KpiCatalog), export financier sous permission dédiée.
2. Défectueux : multidevise (A-046), budget (A-047), balance âgée fournisseurs (A-022 résidu), confidentialité (A-048).

### RH / paie, GED, intégrations, restaurant, terrain
Tests verts ; lecture sans anomalie bloquante ; constats FAIBLES dans `03` et `09` §5.
