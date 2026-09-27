# NEXORA — PHASE 17 — MODÈLE DE DONNÉES POSTGRESQL

**Fondation relationnelle de NEXORA V1.0**
Version 1.0

## 1. Objectif

Définir le modèle relationnel PostgreSQL qui supportera NEXORA : entreprises, utilisateurs, POS,
produits, stocks, achats, ventes, paiements, finance, comptabilité, consolidation, intégrations,
audit et synchronisation offline. Le modèle doit préserver l'intégrité financière et l'isolation
stricte des entreprises.

## 2. Principes de conception

- PostgreSQL comme source transactionnelle principale.
- Identifiants techniques stables, idéalement UUID pour les entités distribuées.
- Numéros métier séparés des identifiants techniques.
- Foreign keys et contraintes d'intégrité.
- Montants en NUMERIC/DECIMAL, jamais en flottant.
- Dates stockées en UTC côté serveur.
- Company scope explicite sur les entités concernées.
- Transactions ACID pour les flux critiques.
- Historisation des opérations financières sensibles.
- Migrations versionnées.
- Index conçus à partir des requêtes réelles.

## 3. Hiérarchie principale

```
GROUP
 └── COMPANY
      ├── ESTABLISHMENT
      │    ├── LOCATION
      │    └── CASH_REGISTER
      ├── USERS / ASSIGNMENTS
      ├── PRODUCTS / INVENTORY
      ├── SALES / PAYMENTS
      ├── PURCHASES
      ├── FINANCE
      └── ACCOUNTING
```

## 4. Conventions

- Tables en snake_case.
- Clés primaires nommées id.
- FK nommées `<table>_id`.
- created_at / updated_at pour les entités appropriées.
- created_by / updated_by lorsque pertinent.
- deleted_at uniquement pour les entités nécessitant archivage logique.
- status sous forme contrôlée.
- currency_code explicite pour les montants multi-devises.
- Indexes sur FK et filtres fréquents.

## 5. Noyau organisationnel

```
groups
companies
establishments
locations
cash_registers
devices
company_settings
establishment_settings
```

- groups : groupes d'entreprises.
- companies : entreprises juridiquement/organisationnellement séparées.
- establishments : sites.
- locations : emplacements, magasins ou entrepôts.
- cash_registers : caisses.
- devices : terminaux autorisés.

## 6. Groupes

- id.
- name.
- code.
- base_currency_code.
- status.
- created_at.
- updated_at.

## 7. Entreprises

- id.
- group_id nullable.
- legal_name.
- display_name.
- registration identifiers selon besoin.
- country_code.
- functional_currency_code.
- timezone.
- status.
- created_at.
- updated_at.

Une entreprise peut être indépendante : `group_id` peut être nul selon la politique retenue.

## 8. Utilisateurs et accès

```
users
roles
permissions
role_permissions
user_company_assignments
user_establishment_assignments
user_cash_register_assignments
user_sessions
user_devices
```

- Séparer identité globale et affectation métier.
- Un utilisateur peut avoir plusieurs rôles selon son périmètre.
- Les permissions sont vérifiées côté serveur.

## 9. Affectations

- user_id.
- company_id.
- establishment_id nullable.
- cash_register_id nullable.
- role_id.
- scope/status.
- starts_at.
- ends_at nullable.

## 10. Produits

```
products
product_categories
product_category_links
product_units
product_unit_conversions
product_barcodes
product_prices
product_taxes
product_custom_fields
product_custom_field_values
```

- Produit générique.
- Catégories multiples si besoin.
- Unités et conversions.
- Plusieurs codes-barres.
- Historique de prix.
- Taxes configurables.
- Champs personnalisés.

## 11. Produits multi-entreprises

Le produit peut être global ou propre à une entreprise selon le modèle retenu. Pour V1, il est
recommandé de distinguer catalogue partagé et configuration locale.

```
PRODUCT → CATALOG CONFIG → COMPANY
```

- Nom/description communs si partagé.
- Prix local.
- Taxe locale.
- Seuil stock local.
- Compte comptable local.
- Référence interne locale.

## 12. Prix

- product_id.
- company_id.
- price_type.
- amount.
- currency_code.
- valid_from.
- valid_to.
- priority.
- status.

Les prix historiques ne doivent pas être écrasés lorsqu'ils sont nécessaires à la reconstitution
des transactions.

## 13. Clients

```
customers
customer_addresses
customer_contacts
customer_credit_accounts
customer_credit_limits
```

- Client global ou entreprise selon modèle.
- Compte crédit par entreprise.
- Limite configurable.
- Conditions de paiement.
- Statut.

## 14. Fournisseurs

```
suppliers
supplier_addresses
supplier_contacts
supplier_payment_terms
supplier_accounts
```

## 15. Stock

```
warehouses
warehouse_locations
inventory_items
inventory_lots
stock_balances
stock_movements
stock_counts
stock_count_items
stock_transfers
stock_transfer_items
```

- Entrepôt.
- Emplacement.
- Article stocké.
- Lot.
- Balance.
- Mouvement.
- Inventaire.
- Transfert.

## 16. Mouvement de stock

- id.
- company_id.
- warehouse_id.
- product_id.
- lot_id nullable.
- movement_type.
- quantity.
- unit_cost nullable.
- reference_type.
- reference_id.
- occurred_at.
- created_by.

Le mouvement constitue la trace principale des changements de stock.

## 17. Lots et expiration

- lot_number.
- product_id.
- company_id.
- manufacture_date nullable.
- expiration_date nullable.
- quantity.
- status.

Les règles FEFO sont appliquées par le domaine stock ; la base fournit les données nécessaires.

## 18. Ventes

```
sales
sale_items
sale_taxes
sale_discounts
sale_returns
sale_return_items
sale_payments
```

- En-tête de vente.
- Lignes.
- Taxes.
- Remises.
- Retours.
- Paiements.

## 19. Vente

- id.
- company_id.
- establishment_id.
- cash_register_id nullable.
- customer_id nullable.
- document_number.
- status.
- subtotal.
- discount_total.
- tax_total.
- grand_total.
- currency_code.
- exchange_rate nullable.
- sold_at.
- created_by.
- source.

## 20. Lignes de vente

- sale_id.
- product_id.
- description snapshot.
- quantity.
- unit_price.
- discount.
- tax.
- line_total.
- lot_id nullable.

Les informations nécessaires à la reconstitution du document doivent être conservées comme
snapshot, même si le produit change ultérieurement.

## 21. Retours et remboursements

- Référence à la vente source lorsque possible.
- Motif.
- Utilisateur.
- Montant.
- Lignes.
- Paiement remboursé.
- Statut.
- Audit.

## 22. Achats

```
purchase_orders
purchase_order_items
goods_receipts
goods_receipt_items
supplier_invoices
supplier_invoice_items
purchase_payments
purchase_returns
```

## 23. Commande d'achat

- Fournisseur.
- Entreprise.
- Établissement.
- Statut.
- Montants.
- Devise.
- Conditions.
- Date.
- Utilisateur.

## 24. Réception

- Commande source.
- Entrepôt.
- Produit.
- Quantité commandée.
- Quantité reçue.
- Lot.
- Coût.
- Date.
- Utilisateur.

## 25. Achats à crédit

Le modèle doit gérer explicitement les achats à crédit et les dettes fournisseurs.

```
SUPPLIER INVOICE → PAYABLE → PAYMENT → RECONCILIATION
```

- supplier_invoice_id.
- supplier_id.
- company_id.
- due_date.
- amount_due.
- amount_paid.
- balance.
- status.

## 26. Créances clients

```
receivables
receivable_installments
receivable_payments
```

- Vente source.
- Client.
- Échéance.
- Montant initial.
- Solde.
- Statut.
- Retard.
- Paiements associés.

## 27. Finance

```
financial_accounts
cash_sessions
cash_movements
bank_accounts
bank_transactions
expenses
expense_categories
budgets
budget_lines
financial_transfers
```

## 28. Comptes financiers

- Compte caisse.
- Compte bancaire.
- Compte mobile money lorsque pertinent.
- Devise.
- Entreprise.
- Numéro/référence masqué selon besoin.
- Statut.

## 29. Sessions de caisse

- cash_register_id.
- opened_by.
- opened_at.
- opening_balance.
- closing_balance_expected.
- closing_balance_actual.
- variance.
- closed_by.
- closed_at.
- status.

## 30. Mouvements financiers

- account_id.
- type.
- amount.
- currency_code.
- reference_type.
- reference_id.
- occurred_at.
- description.
- created_by.

Chaque mouvement financier important doit pouvoir être relié à sa source métier.

## 31. Comptabilité

```
chart_of_accounts
accounting_periods
accounting_journals
journal_entries
journal_entry_lines
accounting_documents
account_mappings
tax_accounts
cost_centers
analytic_dimensions
```

## 32. Plan comptable

- company_id.
- code.
- name.
- account_type.
- parent_id.
- normal_balance.
- is_active.

Un plan groupe peut être représenté séparément et relié par mapping aux plans locaux.

## 33. Périodes comptables

- company_id.
- period_start.
- period_end.
- status.
- closed_at.
- closed_by.
- reopened_at nullable.
- reopened_by nullable.

## 34. Écritures

```
journal_entries
 └── journal_entry_lines
       ├── account
       ├── debit
       ├── credit
       └── dimensions
```

- Journal.
- Numéro.
- Date.
- Description.
- Source.
- Devise.
- Statut.
- Lignes débit/crédit.

## 35. Contraintes comptables

- Une écriture validée doit équilibrer débit et crédit.
- Période clôturée interdite en écriture normale.
- Compte actif requis.
- Source identifiable lorsque générée automatiquement.
- Extourne liée à l'écriture source.
- Validation et audit.

## 36. Documents source → comptabilité

```
SALE → ACCOUNTING EVENT → JOURNAL ENTRY
PURCHASE → ACCOUNTING EVENT → JOURNAL ENTRY
PAYMENT → ACCOUNTING EVENT → JOURNAL ENTRY
EXPENSE → ACCOUNTING EVENT → JOURNAL ENTRY
```

## 37. Consolidation

```
consolidation_profiles
consolidation_periods
consolidation_companies
account_group_mappings
intercompany_transactions
intercompany_matches
consolidation_adjustments
consolidation_eliminations
```

- Profil groupe.
- Période.
- Entreprises incluses.
- Mapping comptes.
- Transactions intercompany.
- Rapprochement.
- Ajustements.
- Éliminations.

## 38. Intercompany

- source_company_id.
- target_company_id.
- transaction_type.
- source_reference.
- target_reference.
- amount.
- currency_code.
- transaction_date.
- match_status.

## 39. Paiements

```
payment_methods
payment_transactions
payment_allocations
payment_provider_accounts
payment_provider_events
refunds
```

- Méthode.
- Transaction.
- Vente/facture source.
- Montant.
- Devise.
- Fournisseur.
- Référence externe.
- Statut.
- Idempotency key.

## 40. Idempotence

- Clé unique par opération concernée.
- event_id fournisseur unique.
- Contrainte DB lorsque possible.
- Rejeu sans double effet.
- Journal des tentatives.

## 41. Banques

```
bank_accounts
bank_transactions
bank_reconciliation_sessions
bank_reconciliation_matches
```

## 42. Intégrations

```
integration_connections
integration_credentials_metadata
integration_events
integration_webhooks
integration_sync_runs
integration_errors
```

Les secrets eux-mêmes ne doivent pas être stockés en clair dans les tables métier ; utiliser un
mécanisme de secrets approprié.

## 43. Offline et synchronisation

```
devices
offline_transactions
sync_outbox
sync_inbox
sync_conflicts
sync_runs
```

- Device.
- Transaction locale.
- Type.
- Payload sécurisé selon architecture.
- Idempotency key.
- Statut.
- Tentatives.
- Dernière erreur.
- Synchronisation.
- Conflit.

## 44. Audit

```
audit_events
security_events
approval_requests
approval_actions
```

- Utilisateur.
- Entreprise.
- Ressource.
- Action.
- Avant/après lorsque pertinent.
- Date.
- Correlation ID.
- Résultat.
- Motif.

## 45. Workflows

```
workflow_definitions
workflow_versions
workflow_instances
workflow_steps
workflow_actions
automation_schedules
automation_runs
```

## 46. Notifications

```
notification_templates
notification_preferences
notifications
notification_deliveries
```

- Canal.
- Destinataire.
- Template.
- Statut.
- Tentatives.
- Erreur.
- Date.

## 47. Documents

```
document_templates
generated_documents
document_references
```

- Template versionné.
- Document généré.
- Type.
- Source.
- Entreprise.
- Chemin/URI.
- Hash lorsque nécessaire.
- Date.

## 48. Paramétrage

```
company_settings
establishment_settings
pos_settings
inventory_settings
accounting_settings
finance_settings
offline_settings
security_settings
```

Les paramètres sensibles doivent être versionnés lorsque leur changement peut modifier le
comportement futur du système.

## 49. Historisation

- Prix.
- Taxes.
- Configuration.
- Rôles.
- Permissions.
- Plan comptable.
- Mapping.
- Taux de change.
- Statuts critiques.
- Paramètres financiers.

## 50. Indexation

- Index sur company_id + dates des tables volumineuses.
- Index sur document_number.
- Index sur références externes.
- Index sur status lorsque sélectif.
- Index sur created_at/occurred_at pour recherche temporelle.
- Index composés basés sur les requêtes réelles.
- Éviter la sur-indexation.

## 51. Contraintes

- NOT NULL pour données obligatoires.
- CHECK pour quantités et montants valides.
- UNIQUE pour identifiants métier appropriés.
- FOREIGN KEY.
- Contraintes de devise.
- Contraintes de statut.
- Contraintes d'idempotence.

## 52. Suppression

- Ne pas supprimer une vente validée.
- Ne pas supprimer une écriture validée.
- Ne pas supprimer l'historique d'audit.
- Archiver/désactiver les entités utilisées.
- Les suppressions physiques doivent être réservées aux données sans valeur historique ou selon
  politique explicite.

## 53. Transactions PostgreSQL

- Vente + lignes + paiement + stock + événements nécessaires dans une unité cohérente lorsque le
  flux l'exige.
- Achat + réception + stock selon workflow.
- Écriture comptable complète atomique.
- Validation financière atomique.
- Rollback en cas d'échec.

## 54. Partitionnement

Le partitionnement ne doit pas être appliqué prématurément. Il pourra être envisagé pour
`audit_events`, `transactions` ou autres tables très volumineuses lorsque les métriques de
production le justifient.

## 55. Row-Level Security

La séparation multi-entreprises doit être garantie principalement par l'autorisation
applicative. PostgreSQL Row-Level Security peut être ajouté comme défense en profondeur dans les
déploiements où cela apporte une valeur réelle.

## 56. Migration

- Migrations versionnées.
- Nom et timestamp/version.
- Backward compatibility pour migrations complexes.
- Backup avant migration critique.
- Tests staging.
- Validation post-migration.

## 57. ERD logique simplifié

```
GROUP
  1 ─── N COMPANY
            │
            ├── N USERS (via assignments)
            ├── N ESTABLISHMENT
            │       └── N CASH_REGISTER
            ├── N PRODUCT
            ├── N WAREHOUSE
            │       └── N STOCK_MOVEMENT
            ├── N SALE ─── N SALE_ITEM
            │      └────── N PAYMENT
            ├── N PURCHASE_ORDER
            ├── N SUPPLIER_INVOICE ─── N PAYMENT
            ├── N RECEIVABLE
            ├── N EXPENSE
            └── N JOURNAL_ENTRY ─── N JOURNAL_ENTRY_LINE
                                     └── ACCOUNT
```

## 58. Règles de cohérence critiques

- Une vente validée ne peut exister sans entreprise.
- Une ligne de vente appartient à une vente.
- Un paiement doit avoir une source ou un contexte financier valide.
- Un mouvement de stock doit avoir une cause identifiable.
- Une dette fournisseur appartient à une entreprise.
- Une créance appartient à une entreprise.
- Une écriture appartient à une période et un journal.
- Les comptes de consolidation ne modifient pas les écritures sources.
- Un événement offline possède une clé unique.

## 59. Scénarios de test DB

- Créer entreprise indépendante.
- Créer groupe et trois entreprises.
- Tester isolation company_id.
- Créer produit et prix local.
- Vendre un produit.
- Vérifier stock.
- Vérifier paiement.
- Vérifier écriture.
- Créer vente à crédit.
- Vérifier créance.
- Créer achat à crédit.
- Vérifier dette.
- Rembourser.
- Retourner stock.
- Créer inventaire.
- Créer intercompany.
- Rapprocher.
- Consolider.
- Envoyer transaction offline deux fois.
- Vérifier idempotence.
- Tester période clôturée.
- Tester audit.

## 60. Critères de sortie

- Schéma logique validé.
- Entités principales définies.
- Relations définies.
- Contraintes définies.
- Multi-entreprises défini.
- POS défini.
- Stock défini.
- Achats défini.
- Ventes défini.
- Crédit défini.
- Finance définie.
- Comptabilité définie.
- Consolidation définie.
- Paiements définis.
- Intégrations définies.
- Offline défini.
- Audit défini.
- Workflows définis.
- Documents définis.
- Paramétrage défini.
- Indexation définie.
- Migrations définies.
- Scénarios de validation définis.

## 61. Étape suivante

Passer à la **Phase 18 — Architecture API .NET / ASP.NET Core**. Elle transformera ce modèle de
données en contrats API, modules, endpoints, DTO, services applicatifs, autorisation, validation,
gestion des erreurs et flux métier.

---

**NEXORA V1.0 — PHASE 17 VALIDÉE → PHASE 18 : API .NET / ASP.NET CORE**
