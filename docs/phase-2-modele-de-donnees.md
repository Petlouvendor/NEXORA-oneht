# NEXORA — PHASE 2 — MODÈLE DE DONNÉES

**Architecture logique des données, relations et règles d'intégrité**
**Version 1.0**

> Document de référence. Le schéma physique PostgreSQL est dérivé de ce modèle dans
> [`database/migrations/`](../database/migrations/) (voir [`database/README.md`](../database/README.md)).

---

## 1. Objectif

Transformer la cartographie fonctionnelle de NEXORA V1.0 en un modèle de données cohérent, extensible et adapté à PostgreSQL. Le modèle doit supporter une petite entreprise comme un groupe multi-entreprises, tout en conservant une source de vérité unique pour les transactions commerciales, financières et comptables.

### Principes

- Chaque donnée possède un propriétaire fonctionnel clair.
- Les transactions validées sont immuables ou corrigées par des opérations traçables.
- Les données sont isolées par entreprise selon le périmètre d'accès.
- Les modules partagent les mêmes référentiels et transactions.
- La comptabilité ne doit pas devenir une base parallèle aux opérations métier.
- Le modèle doit supporter l'évolution sans créer une version par secteur.

## 2. Architecture logique

```
TENANT / GROUPE
   ↓
ENTREPRISE
   ↓
ÉTABLISSEMENT
   ├── MAGASIN
   ├── ENTREPÔT
   └── CAISSE / TERMINAL

UTILISATEUR
   ↓
RÔLE → PERMISSION → PÉRIMÈTRE

RÉFÉRENTIELS
   ├── Produits
   ├── Clients
   ├── Fournisseurs
   ├── Taxes
   ├── Devises
   └── Comptes

TRANSACTIONS
   ├── Ventes → Paiements / Créances
   ├── Achats → Paiements / Dettes
   ├── Stock → Mouvements
   └── Finance → Écritures comptables
```

## 3. Conventions générales

- Identifiant primaire recommandé : UUID.
- Chaque table métier importante possède `created_at` et `updated_at`.
- Les montants utilisent `NUMERIC`/`DECIMAL` et jamais `FLOAT`.
- Les dates/heures sont stockées avec une stratégie timezone cohérente.
- Les statuts sont contrôlés par des valeurs autorisées.
- Les données validées ne sont pas supprimées physiquement sans procédure d'archivage.
- Les clés étrangères sont explicites.
- Les index sont créés selon les recherches et contraintes réelles.

## 4. Groupes et entreprises

**`group`** — id · name · legal_name · status · default_currency_id · created_at · updated_at

**`company`** — id · group_id · name · legal_name · tax_identifier · status · main_activity_type · default_currency_id · timezone · settings · created_at · updated_at

**Principe** : `group_id` peut être nullable pour une entreprise indépendante si le produit autorise le fonctionnement hors groupe. Une entreprise reste l'unité principale de séparation des données commerciales, financières et comptables.

## 5. Structure opérationnelle

**`establishment`** — id · company_id · name · code · address · phone · status · settings
**`store`** — id · establishment_id · name · code · status
**`warehouse`** — id · establishment_id · name · code · type · status
**`pos_terminal`** — id · store_id · name · code · device_identifier · status · configuration

```
COMPANY 1 ─── N ESTABLISHMENT
ESTABLISHMENT 1 ─── N STORE
ESTABLISHMENT 1 ─── N WAREHOUSE
STORE 1 ─── N POS_TERMINAL
```

## 6. Utilisateurs, rôles et permissions

**`user`** — id · email/username · password_hash ou identité externe · status · mfa_enabled · last_login_at · created_at · updated_at
**`role`** — id · name · description · system_role · status
**`permission`** — id · code · name · description · module · action
**`user_role`** — user_id · role_id · scope_id
**`access_scope`** — id · scope_type · group_id · company_id · establishment_id · store_id · warehouse_id · pos_terminal_id

Le scope représente le contexte d'application de la permission. Il doit être possible d'avoir le même utilisateur avec des rôles différents sur des entreprises différentes.

```
USER → USER_ROLE → ROLE → PERMISSION
                  ↓
                SCOPE
```

## 7. Référentiels

**`currency`** — id · code · name · symbol · decimal_places · status
**`exchange_rate`** — id · base_currency_id · quote_currency_id · rate · effective_at · source · created_at
**`tax`** — id · company_id · name · code · rate · type · status
**`payment_method`** — id · company_id · name · type · requires_reference · status
**`unit`** — id · company_id · name · code · precision
**`category` / `brand`** — rattachées au référentiel produit ; globales au périmètre autorisé ou propres à une entreprise selon la configuration.

## 8. Produits

**`product`** — id · company_id · sku · barcode · name · description · category_id · brand_id · unit_id · product_type · cost_method · status · tax_configuration · created_at · updated_at
**`product_price`** — id · product_id · price_type · customer_group_id · quantity_min · currency_id · amount · valid_from · valid_to
**`product_supplier`** — product_id · supplier_id · supplier_sku · purchase_price · currency_id · lead_time_days · is_preferred

Les prix doivent être historisables. Une modification de prix ne doit pas réécrire silencieusement l'historique des transactions.

## 9. Clients

**`customer`** — id · company_id · customer_number · name · type · phone · email · address · tax_identifier · status · credit_limit · default_currency_id
**`customer_account`** — id · customer_id · currency_id · credit_limit · payment_terms_id · current_balance · status
**`customer_payment_terms`** — id · company_id · name · days · description

Le solde client doit être dérivable des transactions validées. Un champ de solde matérialisé peut être utilisé pour la performance, mais il doit rester réconciliable avec les écritures.

## 10. Fournisseurs

**`supplier`** — id · company_id · supplier_number · name · type · phone · email · address · tax_identifier · status · credit_limit · default_currency_id
**`supplier_account`** — id · supplier_id · currency_id · credit_limit · payment_terms_id · current_balance · status
**`supplier_payment_terms`** — id · company_id · name · days · description

## 11. Stocks

**`inventory_location`** — id · company_id · establishment_id · warehouse_id · store_id · name · location_type · status
**`stock_balance`** — id · product_id · inventory_location_id · quantity_on_hand · quantity_reserved · quantity_available · average_cost · updated_at
**`stock_movement`** — id · product_id · inventory_location_id · movement_type · quantity · unit_cost · reference_type · reference_id · occurred_at · user_id
**`lot`** — id · product_id · lot_number · expiration_date · manufacture_date · status
**`lot_stock`** — lot_id · inventory_location_id · quantity_on_hand · quantity_reserved

```
STOCK_BALANCE      = ÉTAT COURANT
STOCK_MOVEMENT     = HISTORIQUE DES MOUVEMENTS
LOT / LOT_STOCK    = TRAÇABILITÉ OPTIONNELLE PAR PRODUIT
```

## 12. Achats

**`purchase_order`** — id · company_id · supplier_id · establishment_id · warehouse_id · document_number · status · currency_id · order_date · expected_date · subtotal · tax_total · total
**`purchase_order_line`** — id · purchase_order_id · product_id · quantity_ordered · unit_price · tax_id · discount · total
**`goods_receipt`** — id · company_id · supplier_id · warehouse_id · purchase_order_id · document_number · status · received_at
**`goods_receipt_line`** — id · goods_receipt_id · product_id · quantity_received · unit_cost · lot_id
**`supplier_invoice`** — id · company_id · supplier_id · purchase_order_id · document_number · invoice_date · due_date · currency_id · subtotal · tax_total · total · status
**`supplier_invoice_line`** — id · supplier_invoice_id · product_id · quantity · unit_price · tax_id · discount · total

## 13. Achats à crédit et dettes

La facture fournisseur devient une obligation lorsqu'elle est validée et non entièrement payée.

```
SUPPLIER_INVOICE → SUPPLIER_PAYABLE → PAYMENT_ALLOCATION → PAYMENT
```

**`supplier_payable`** — id · supplier_invoice_id · supplier_id · currency_id · original_amount · paid_amount · remaining_amount · issue_date · due_date · status

**`payment`** — objet financier générique pouvant être affecté à une ou plusieurs créances/dettes selon son contexte.

**`payment_allocation`** — id · payment_id · receivable_id (nullable) · payable_id (nullable) · allocated_amount · currency_id · allocated_at

Un paiement partiel doit être possible. Le solde ne doit jamais devenir négatif.

## 14. Ventes

**`sales_order` / `sale`** — id · company_id · establishment_id · store_id · pos_terminal_id · customer_id (nullable) · document_number · status · sale_type · currency_id · exchange_rate_id (nullable) · sold_at · subtotal · discount_total · tax_total · total
**`sale_line`** — id · sale_id · product_id · quantity · unit_price · discount · tax_id · total · cost_snapshot
**`sale_payment`** — id · sale_id · payment_id · amount · currency_id

Le coût au moment de la vente doit pouvoir être conservé pour calculer correctement la marge historique.

## 15. Ventes à crédit et créances

```
SALE → CUSTOMER_RECEIVABLE → PAYMENT_ALLOCATION → PAYMENT
```

**`customer_receivable`** — id · sale_id · customer_id · currency_id · original_amount · paid_amount · remaining_amount · issue_date · due_date · status

Statuts : `OPEN`, `PARTIALLY_PAID`, `PAID`, `OVERDUE`, `CANCELLED` selon les règles métier.

Une vente à crédit doit créer une créance. Les paiements partiels réduisent cette créance et alimentent la trésorerie lorsqu'ils sont effectivement encaissés.

## 16. Caisse

**`cash_register`** — id · company_id · store_id · name · code · currency_id · status
**`cash_session`** — id · cash_register_id · pos_terminal_id · user_id · opened_at · closed_at · opening_balance · expected_balance · counted_balance · difference · status
**`cash_movement`** — id · cash_session_id · movement_type · amount · currency_id · reference_type · reference_id · occurred_at · user_id

La session de caisse représente le cycle opérationnel d'un caissier. Les écarts doivent être conservés et auditables.

## 17. Banques et comptes financiers

**`financial_account`** — id · company_id · account_type · name · account_number_masked · currency_id · status
**`financial_transaction`** — id · financial_account_id · transaction_type · amount · currency_id · reference_type · reference_id · occurred_at · status

Les comptes peuvent représenter caisse, banque, Mobile Money ou autres comptes financiers configurés.

## 18. Dépenses

**`expense`** — id · company_id · establishment_id · financial_account_id · category_id · supplier_id (nullable) · document_number · description · amount · tax_amount · currency_id · expense_date · status · approved_by

Une dépense peut nécessiter une approbation selon les seuils de l'entreprise.

## 19. Comptabilité

**`chart_of_accounts`** — id · company_id · name · version · status
**`account`** — id · chart_of_accounts_id · code · name · account_type · parent_account_id · is_postable · status
**`accounting_period`** — id · company_id · start_date · end_date · status · closed_at · closed_by
**`journal`** — id · company_id · code · name · journal_type · status
**`journal_entry`** — id · company_id · journal_id · accounting_period_id · entry_number · entry_date · source_type · source_id · description · status
**`journal_entry_line`** — id · journal_entry_id · account_id · debit · credit · currency_id · exchange_rate_id · third_party_type · third_party_id · cost_center_id (nullable)

```
JOURNAL_ENTRY 1 → N JOURNAL_ENTRY_LINE
SUM(DEBIT) = SUM(CREDIT)
```

## 20. Règles comptables et génération

Les règles de génération des écritures doivent être centralisées dans le moteur financier/comptable. Une interface POS ne doit pas contenir sa propre logique comptable.

- Chaque écriture possède une source métier identifiable.
- Une écriture validée ne doit pas être modifiée directement.
- Une correction passe par une écriture d'extourne/correction selon la règle retenue.
- Les comptes utilisés doivent être postables.
- Une période clôturée interdit les nouvelles écritures non autorisées.
- Le total débit doit toujours être égal au total crédit.

## 21. Centres de coûts et analytique

**`cost_center`** — id · company_id · code · name · parent_id · status
**`entry_analytic_allocation`** — id · journal_entry_line_id · cost_center_id · percentage · amount

Cette couche permet de suivre la rentabilité par établissement, activité, département ou centre de coûts sans modifier le noyau comptable.

## 22. Intercompany

**`intercompany_transaction`** — id · group_id · source_company_id · target_company_id · transaction_type · source_reference_type · source_reference_id · target_reference_type · target_reference_id · amount · currency_id · status

Les transactions inter-entreprises doivent être identifiables dans les deux sociétés afin de faciliter rapprochement et consolidation.

## 23. Consolidation

**`consolidation_profile`** — id · group_id · name · currency_id · status
**`consolidation_scope`** — id · consolidation_profile_id · company_id · effective_from · effective_to
**`consolidation_adjustment`** — id · consolidation_profile_id · company_id (nullable) · account_id · amount · currency_id · reason · created_by · created_at

La consolidation part des comptabilités individuelles et applique les règles d'élimination/ajustement sans altérer les livres des entreprises sources.

## 24. Documents et numérotation

**`document_sequence`** — id · company_id · document_type · prefix · current_number · year · establishment_id (nullable) · format

La numérotation doit être configurable et éviter les collisions entre entreprises, établissements et types de documents.

## 25. Audit

**`audit_log`** — id · company_id (nullable) · user_id · action · entity_type · entity_id · old_values · new_values · ip_address · device_id · occurred_at

L'audit doit répondre à : qui, quoi, quand, où, sur quelle entreprise et quelle valeur a été modifiée.

## 26. Notifications et alertes

**`notification`** — id · user_id · company_id (nullable) · type · severity · title · message · reference_type · reference_id · read_at · created_at
**`alert_rule`** — id · company_id · rule_type · threshold · configuration · status

Les alertes sont calculées selon les règles métier et respectent le périmètre de l'utilisateur.

## 27. Workflows d'approbation

**`approval_request`** — id · company_id · request_type · reference_type · reference_id · requested_by · status · requested_at · completed_at
**`approval_action`** — id · approval_request_id · approver_id · action · comment · created_at

Un workflow doit permettre plusieurs niveaux d'approbation lorsque l'entreprise le configure.

## 28. Offline et synchronisation

**`sync_device`** — id · company_id · device_identifier · device_type · last_sync_at · status
**`sync_operation`** — id · device_id · operation_uuid · entity_type · entity_id · operation_type · payload · created_at · synced_at · status · error_code

- Chaque opération hors ligne possède un `operation_uuid` globalement unique.
- La synchronisation doit être idempotente : rejouer une opération ne doit pas créer une deuxième vente ou un deuxième paiement.
- Les conflits doivent être détectés et traités selon des règles explicites.

## 29. Suppression, archivage et historique

- Les produits, clients, fournisseurs et utilisateurs peuvent être archivés lorsqu'ils ne doivent plus apparaître dans les nouveaux processus.
- Les transactions financières et comptables validées ne sont pas supprimées silencieusement.
- Les documents annulés conservent leur identité et leur historique.
- Les données archivées restent accessibles aux utilisateurs autorisés selon les règles de rétention.

## 30. Relations principales

```
GROUP 1 ─── N COMPANY
COMPANY 1 ─── N ESTABLISHMENT
ESTABLISHMENT 1 ─── N STORE / WAREHOUSE
STORE 1 ─── N POS_TERMINAL
COMPANY 1 ─── N PRODUCT / CUSTOMER / SUPPLIER
PRODUCT 1 ─── N STOCK_MOVEMENT
SUPPLIER 1 ─── N PURCHASE_ORDER / SUPPLIER_INVOICE
CUSTOMER 1 ─── N SALE / CUSTOMER_RECEIVABLE
SALE 1 ─── N SALE_LINE
SALE 1 ─── N SALE_PAYMENT
SALE 1 ─── 0..1 CUSTOMER_RECEIVABLE
SUPPLIER_INVOICE 1 ─── 0..1 SUPPLIER_PAYABLE
PAYMENT N ─── N RECEIVABLE/PAYABLE via PAYMENT_ALLOCATION
JOURNAL_ENTRY 1 ─── N JOURNAL_ENTRY_LINE
COMPANY 1 ─── N JOURNAL_ENTRY
GROUP 1 ─── N INTERCOMPANY_TRANSACTION
```

## 31. Contraintes d'intégrité critiques

- `company_id` doit être cohérent entre les objets liés lorsque le modèle l'exige.
- Un utilisateur ne doit pas pouvoir traverser une relation vers une entreprise hors périmètre.
- Une ligne de vente doit référencer un produit valide.
- Une réception ne peut augmenter le stock sans ligne de réception valide.
- Une vente validée ne peut être créée sans contexte d'entreprise et de devise.
- Un paiement affecté ne peut dépasser le montant disponible du paiement ou le solde de la créance/dette.
- Une créance ne peut avoir un solde négatif.
- Une dette ne peut avoir un solde négatif.
- Débit total = crédit total pour chaque écriture comptable validée.
- Une période clôturée ne reçoit pas d'écriture normale.
- Une opération offline est idempotente via son identifiant unique.
- Les références documentaires doivent être uniques dans leur périmètre.

## 32. Scénarios de données

**Vente comptant**
```
SALE → SALE_LINE → STOCK_MOVEMENT → PAYMENT → CASH/FINANCIAL_TRANSACTION → JOURNAL_ENTRY
```

**Vente à crédit**
```
SALE → SALE_LINE → STOCK_MOVEMENT → CUSTOMER_RECEIVABLE → JOURNAL_ENTRY
PAYMENT → PAYMENT_ALLOCATION → CUSTOMER_RECEIVABLE ↓ → CASH/BANK ↑ → JOURNAL_ENTRY
```

**Achat à crédit**
```
PURCHASE → RECEIPT → STOCK_MOVEMENT → SUPPLIER_INVOICE → SUPPLIER_PAYABLE → JOURNAL_ENTRY
PAYMENT → PAYMENT_ALLOCATION → SUPPLIER_PAYABLE ↓ → CASH/BANK ↓ → JOURNAL_ENTRY
```

**Transfert de stock**
```
STOCK_LOCATION A → MOVEMENT OUT → MOVEMENT IN → STOCK_LOCATION B
```

**Intercompany**
```
COMPANY A TRANSACTION ↔ INTERCOMPANY ↔ COMPANY B TRANSACTION → RECONCILIATION → CONSOLIDATION
```

## 33. Indexation PostgreSQL — principes

- Index sur toutes les clés étrangères fréquemment filtrées.
- Index composés sur `company_id` + date/statut pour les listes principales.
- Index uniques sur SKU/code-barres selon les règles métier.
- Index sur `document_number` dans son périmètre.
- Index sur `due_date` + `status` pour créances/dettes.
- Index sur `occurred_at` pour mouvements et audit.
- Index sur `operation_uuid` pour idempotence offline.
- Analyser les plans d'exécution avant de multiplier les index.

## 34. Sécurité des données

- Ne pas utiliser uniquement l'interface pour sécuriser le périmètre.
- Appliquer le contrôle d'accès côté serveur.
- Envisager Row-Level Security PostgreSQL pour les données fortement multi-tenant si l'architecture le justifie.
- Chiffrer les secrets et données sensibles appropriées.
- Ne jamais stocker les mots de passe en clair.
- Journaliser les actions sensibles.
- Protéger les données financières contre les modifications non autorisées.

## 35. Extensibilité sectorielle

```
CORE PRODUCT
   ├── PHARMACY_PRODUCT / LOT / PRESCRIPTION
   ├── RESTAURANT_RECIPE / INGREDIENT / TABLE
   ├── RETAIL_WEIGHT / PROMOTION
   └── DISTRIBUTION_PRICE / DELIVERY
```

Cette stratégie permet d'ajouter un nouveau secteur sans réécrire les tables de vente, paiement, stock, finance et comptabilité.

## 36. Source de vérité

- **Produit** : référentiel produit.
- **Stock** : mouvements validés + état courant réconciliable.
- **Vente** : transaction de vente validée.
- **Créance** : obligation client issue d'une vente ou d'un document autorisé.
- **Dette** : obligation fournisseur issue d'une facture validée.
- **Paiement** : transaction financière effectivement enregistrée.
- **Comptabilité** : écritures validées.
- **Rapports** : lectures dérivées des données sources, jamais des saisies manuelles parallèles.

## 37. Critères de validation de la Phase 2

- Toutes les entités fonctionnelles principales sont identifiées.
- Les relations et cardinalités principales sont définies.
- Les flux de crédit client et fournisseur sont représentés.
- Les paiements partiels sont représentés.
- Le stock possède un historique de mouvements.
- La comptabilité est intégrée aux transactions.
- Le multi-entreprises et l'intercompany sont représentés.
- La consolidation n'altère pas les comptabilités sources.
- L'offline dispose d'une stratégie d'idempotence.
- Les contraintes financières critiques sont explicites.
- Le modèle est exploitable pour une conception PostgreSQL détaillée.

## 38. Livrable de la phase suivante

La validation de ce document permet de passer à la **Phase 3 : Architecture et sécurité**. Avant le codage, une version finale du schéma physique PostgreSQL est dérivée de ce modèle logique, avec types SQL exacts, contraintes, index, migrations et stratégie de Row-Level Security lorsque retenue.

---

## Annexe A — Schéma physique PostgreSQL (livré)

Le modèle logique ci-dessus est implémenté dans [`database/migrations/`](../database/migrations/)
(`V001` → `V021` pour la Phase 2 ; `V022`–`V023` ajoutées en Phase 4). Voir
[`database/README.md`](../database/README.md).

**État validé** — 101 tables · RLS sur 93 tables · seed (144 permissions, 12 rôles système).
Test fonctionnel `database/scripts/smoke_test.sql` : contrôles passés (scénarios §32 + rejets §31).

### Découpage des migrations

| # | Fichier | Contenu |
|---|---|---|
| V001 | extensions_and_common | schéma `nexora`, extensions, domaines montants, `set_updated_at`, helpers RLS, `schema_migration` |
| V002 | currency | `currency`, `exchange_rate` (global) |
| V003 | org_structure | `business_group`, `company`, `establishment`, `store`, `warehouse`, `pos_terminal` |
| V004 | identity_access | `app_user`, `role`, `permission`, `role_permission`, `access_scope`, `user_role` |
| V005 | reference_data | `tax`, `payment_method`, `unit`, `product_category`, `brand`, `customer_group`, conditions de règlement, `expense_category` |
| V006 | partners | `customer` / `customer_account`, `supplier` / `supplier_account` |
| V007 | products | `product`, `product_price`, `product_supplier` |
| V008 | inventory | `inventory_location`, `stock_balance`, `stock_movement`, `lot`, `lot_stock` |
| V009 | purchasing | `purchase_order(_line)`, `goods_receipt(_line)`, `supplier_invoice(_line)` |
| V010 | sales | `sale`, `sale_line`, `sale_return(_line)` |
| V011 | finance_and_cash | `financial_account`, `financial_transaction`, `cash_register`, `cash_session`, `cash_movement`, `expense`, `budget(_line)`, `bank_statement(_line)` |
| V012 | payments_receivables_payables | `payment`, `sale_payment`, `customer_receivable`, `supplier_payable`, `payment_allocation` |
| V013 | accounting | `chart_of_accounts`, `account`, `accounting_period`, `journal`, `journal_entry(_line)`, `cost_center`, `entry_analytic_allocation` |
| V014 | intercompany_consolidation | `intercompany_transaction`, `consolidation_profile` / `_scope` / `_adjustment` |
| V015 | documents_and_sequences | `document_sequence` + `next_document_number()`, `issued_document` |
| V016 | audit_notifications_approvals | `audit_log`, `notification`, `alert_rule`, `approval_policy`, `approval_request` / `_action` |
| V017 | offline_sync | `sync_device`, `sync_operation`, `sync_conflict` |
| V018 | sector_and_crm_extensions | pharmacie, restaurant, retail (promotions / codes-barres), fidélité, livraison |
| V019 | financial_integrity | triggers : équilibre, comptes postables, périodes clôturées, immuabilité, affectations, stock, anti-suppression, audit |
| V020 | row_level_security | rôle `nexora_app`, politiques RLS entreprise / groupe / audit / notifications |
| V021 | seed_reference_data | devises, catalogue de permissions, rôles système + permissions |

### Écarts assumés vs modèle logique

| Modèle logique | Schéma physique | Raison |
|---|---|---|
| `group` | `business_group` | `GROUP` réservé SQL |
| `user` | `app_user` | `USER` réservé SQL |
| `category` | `product_category` | clarté ; `brand` conservé |
| `sales_order` / `sale` | `sale` (+ `sale_line`, `sale_payment`, `sale_return`) | une seule entité de vente |
| catégories/marques « globales » | propres à l'entreprise | FK composites d'isolation ; partage = copie de modèles |
| `payment_allocation` | + `company_id` | uniformité RLS + FK composite |
| lignes (`*_line`) | + `company_id` non nul + FK `(parent_id, company_id)` | §31 : cohérence entreprise garantie par le moteur |

### Contrat d'exécution pour l'API (§34)

Par transaction : `SET LOCAL nexora.company_ids = '…'` (+ `group_ids`, `user_id`).
Le rôle applicatif `nexora_app` n'a pas `BYPASSRLS`. Drapeaux d'exception :
`nexora.allow_closed_period`, `nexora.allow_hard_delete`.

---

**NEXORA — Modèle de données V1.0 → Architecture & Sécurité**
