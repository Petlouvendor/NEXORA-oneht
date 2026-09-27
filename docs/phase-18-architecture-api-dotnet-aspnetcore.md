# NEXORA — PHASE 18 — ARCHITECTURE API .NET / ASP.NET CORE

**Contrats API, modules applicatifs, sécurité et flux métier**
Version 1.0

## 1. Objectif

Définir l'architecture de l'API NEXORA qui servira de frontière entre le frontend
React/TypeScript, le POS, les clients offline et le cœur métier .NET. L'API doit garantir
cohérence financière, sécurité multi-entreprises, validation, audit, idempotence et évolutivité.

## 2. Principes

- API orientée cas d'utilisation métier.
- Versionnement explicite.
- DTO distincts des entités PostgreSQL.
- Autorisation côté serveur.
- Validation systématique.
- Transactions pour opérations critiques.
- Idempotence pour opérations rejouables.
- Erreurs structurées.
- Correlation ID.
- Pagination et filtrage.
- Aucune logique financière critique dans le frontend.

## 3. Architecture

```
React / POS / Offline Client
          ↓ HTTPS
     ASP.NET Core API
          ↓
┌──────────────────────────────┐
│ API / Application / Domain   │
└──────────────┬───────────────┘
               ↓
         Infrastructure
               ↓
          PostgreSQL
               ↓
   Jobs / Integrations / Files
```

## 4. Structure des projets

```
src/
├── Nexora.Api
├── Nexora.Application
├── Nexora.Domain
├── Nexora.Infrastructure
├── Nexora.Contracts
├── Nexora.Worker
└── Nexora.Tests
```

- Api : endpoints, middleware, auth HTTP.
- Application : use cases, orchestration.
- Domain : règles métier.
- Infrastructure : DB et services externes.
- Contracts : DTO et contrats publics.
- Worker : tâches asynchrones.
- Tests : unitaires, intégration et end-to-end.

## 5. Organisation des modules

```
/Modules
  /Identity
  /Organization
  /Catalog
  /Inventory
  /Sales
  /Purchasing
  /Customers
  /Suppliers
  /Payments
  /Finance
  /Accounting
  /Reporting
  /Consolidation
  /Workflows
  /Integrations
  /Notifications
  /Documents
  /Audit
  /Synchronization
```

## 6. Versionnement API

- Base : /api/v1.
- Ne pas casser silencieusement un contrat public.
- Nouvelle version lorsque changement incompatible.
- Documenter dépréciations.
- Conserver compatibilité pendant période de migration définie.

```
GET /api/v1/companies
```

## 7. Authentification

- Connexion.
- Refresh/session selon mécanisme retenu.
- Révocation.
- MFA.
- Réinitialisation.
- Réauthentification actions sensibles.
- Identification appareil.
- Expiration.

## 8. Contexte d'accès

```
AUTHENTICATED USER
       ↓
ROLES
       ↓
PERMISSIONS
       ↓
SCOPE
       ↓
COMPANY / ESTABLISHMENT / CASH REGISTER
       ↓
RESOURCE
```

L'API ne doit jamais considérer `company_id` fourni par le navigateur comme une preuve d'accès.

## 9. Middleware

- Correlation ID.
- Authentication.
- Authorization.
- Exception handling.
- Request logging sécurisé.
- Rate limiting.
- Tenant/scope context.
- Idempotency lorsque applicable.
- Audit context.

## 10. Validation

- Validation syntaxique.
- Validation métier.
- Validation permissions.
- Validation période.
- Validation stock.
- Validation montant.
- Validation devise.
- Validation statut.
- Validation idempotence.

## 11. Gestion des erreurs

```
{
  code,
  message,
  details,
  correlationId
}
```

- Messages utilisateur sûrs.
- Détails techniques dans logs, pas dans réponse publique.
- Codes stables pour le frontend.
- HTTP status approprié.
- Erreurs de conflit distinctes.

## 12. Idempotency

```
POST /sales
Idempotency-Key: <unique-key>

→ première requête : traitement
→ répétition : même résultat
→ aucun double effet
```

- Ventes offline.
- Paiements.
- Webhooks.
- Import/synchronisation.
- Opérations financières rejouables.

## 13. API Organization

```
GET    /api/v1/groups
POST   /api/v1/groups
GET    /api/v1/companies
POST   /api/v1/companies
GET    /api/v1/companies/{id}
PATCH  /api/v1/companies/{id}
GET    /api/v1/companies/{id}/establishments
```

- Toutes les routes respectent le scope.
- Création entreprise contrôlée.
- Activation/archivage audités.

## 14. API Users & Roles

```
GET/POST /api/v1/users
GET/POST /api/v1/roles
POST /api/v1/users/{id}/assignments
DELETE /api/v1/users/{id}/assignments/{assignmentId}
GET /api/v1/permissions
```

## 15. API Catalog

```
GET    /api/v1/products
POST   /api/v1/products
GET    /api/v1/products/{id}
PATCH  /api/v1/products/{id}
GET    /api/v1/products/{id}/prices
POST   /api/v1/products/{id}/barcodes
GET    /api/v1/categories
```

- Recherche rapide.
- Pagination.
- Filtres.
- Prix avec dates d'effet.
- Configuration locale entreprise.

## 16. API Inventory

```
GET  /api/v1/inventory
GET  /api/v1/stock-movements
POST /api/v1/stock-adjustments
POST /api/v1/stock-transfers
POST /api/v1/inventory-counts
GET  /api/v1/lots
```

- Mouvements contrôlés.
- Aucun ajustement silencieux.
- Lots/expiration.
- FEFO lorsque activé.
- Audit.

## 17. API POS / Sales

```
POST /api/v1/sales
GET  /api/v1/sales/{id}
POST /api/v1/sales/{id}/payments
POST /api/v1/sales/{id}/returns
POST /api/v1/sales/{id}/refunds
POST /api/v1/sales/{id}/cancel
GET  /api/v1/sales/search
```

## 18. Création d'une vente

```
REQUEST
 ↓
AUTHORIZATION
 ↓
VALIDATION
 ↓
PRICE/TAX
 ↓
INVENTORY CHECK
 ↓
SALE PERSISTENCE
 ↓
STOCK MOVEMENTS
 ↓
PAYMENT / RECEIVABLE
 ↓
ACCOUNTING EVENT
 ↓
AUDIT
 ↓
RESPONSE
```

Le traitement exact peut être orchestré par application/domain selon le scénario, mais les
invariants financiers doivent être garantis.

## 19. API Payments

```
POST /api/v1/payments
GET  /api/v1/payments/{id}
POST /api/v1/payments/{id}/capture
POST /api/v1/payments/{id}/refund
POST /api/v1/payments/{id}/cancel
```

- Référence interne.
- Référence fournisseur.
- Statut.
- Montant.
- Devise.
- Source.
- Idempotence.
- Audit.

## 20. API Customers / Credit

```
GET  /api/v1/customers
POST /api/v1/customers
GET  /api/v1/customers/{id}/credit
POST /api/v1/receivables/{id}/payments
GET  /api/v1/receivables
GET  /api/v1/receivables/overdue
```

- Limite crédit.
- Solde.
- Échéances.
- Paiements partiels.
- Historique.
- Permissions.

## 21. API Suppliers / Purchases

```
GET/POST /api/v1/suppliers
GET/POST /api/v1/purchase-orders
POST /api/v1/purchase-orders/{id}/receive
GET/POST /api/v1/supplier-invoices
GET /api/v1/payables
POST /api/v1/payables/{id}/payments
```

## 22. Achats à crédit

Le flux doit permettre de créer la dette fournisseur, suivre les échéances et enregistrer les
règlements sans perdre le lien avec l'achat source.

```
PURCHASE → RECEIPT → INVOICE → PAYABLE → PAYMENT → ACCOUNTING
```

## 23. API Finance

```
GET/POST /api/v1/financial-accounts
GET/POST /api/v1/expenses
GET/POST /api/v1/cash-sessions
POST /api/v1/financial-transfers
GET /api/v1/cash-reconciliation
GET /api/v1/bank-transactions
```

## 24. API Accounting

```
GET  /api/v1/accounts
GET  /api/v1/accounting-periods
POST /api/v1/journal-entries
GET  /api/v1/journal-entries
POST /api/v1/journal-entries/{id}/post
POST /api/v1/journal-entries/{id}/reverse
POST /api/v1/accounting-periods/{id}/close
POST /api/v1/accounting-periods/{id}/reopen
```

- Reopen fortement contrôlé.
- Écritures équilibrées.
- Périodes contrôlées.
- Source métier identifiable.
- Audit.

## 25. API Reporting

```
GET /api/v1/reports/sales
GET /api/v1/reports/inventory
GET /api/v1/reports/cash
GET /api/v1/reports/receivables
GET /api/v1/reports/payables
GET /api/v1/reports/profit-loss
GET /api/v1/reports/balance-sheet
```

- Filtres par période.
- Entreprise.
- Établissement.
- Secteur.
- Comparaison.
- Export selon permissions.

## 26. API Groupe & Consolidation

```
GET  /api/v1/group/dashboard
GET  /api/v1/group/companies
POST /api/v1/consolidations
GET  /api/v1/consolidations/{id}
POST /api/v1/consolidations/{id}/prepare
POST /api/v1/consolidations/{id}/validate
POST /api/v1/consolidations/{id}/publish
```

- Accessible uniquement au scope groupe autorisé.
- Mapping et éliminations contrôlés.
- Audit.

## 27. API Intercompany

```
POST /api/v1/intercompany/transactions
GET  /api/v1/intercompany/matches
POST /api/v1/intercompany/matches/{id}/match
GET  /api/v1/intercompany/exceptions
```

## 28. API Workflows

```
GET/POST /api/v1/workflows
POST /api/v1/workflows/{id}/test
GET /api/v1/approvals
POST /api/v1/approvals/{id}/approve
POST /api/v1/approvals/{id}/reject
```

- Approbateur doit posséder permission.
- Action auditée.
- Expiration.
- Double approbation si règle.

## 29. API Configuration

```
GET /api/v1/settings
PATCH /api/v1/settings
GET /api/v1/configuration-versions
POST /api/v1/configuration-preview
POST /api/v1/configuration-publish
```

Les endpoints de configuration doivent appliquer les règles de scope et de versionnement
définies en Phase 14.

## 30. API Integrations

```
GET /api/v1/integrations
POST /api/v1/integrations/{id}/test
POST /api/v1/integrations/{id}/activate
POST /api/v1/integrations/{id}/disable
GET /api/v1/integrations/{id}/events
```

## 31. Webhooks entrants

```
POST /api/v1/webhooks/{provider}/{event}
```

- Vérification signature.
- Idempotence.
- Validation payload.
- Traitement asynchrone lorsque pertinent.
- Audit.
- Réponse rapide.

## 32. Webhooks sortants

- Événement.
- Endpoint abonné.
- Signature.
- Tentatives.
- Backoff.
- Statut.
- Idempotency key.
- Journal.

## 33. API Offline / Sync

```
POST /api/v1/sync/push
POST /api/v1/sync/pull
POST /api/v1/sync/ack
GET  /api/v1/sync/status
GET  /api/v1/sync/conflicts
POST /api/v1/sync/conflicts/{id}/resolve
```

- Device authentifié.
- Transactions uniques.
- Version/sequence.
- Conflits explicites.
- Aucun écrasement silencieux.

## 34. API Audit

```
GET /api/v1/audit-events
GET /api/v1/security-events
GET /api/v1/approvals/{id}/history
```

- Filtrage par scope.
- Accès lecture spécialisé.
- Export contrôlé.
- Audit des consultations sensibles si politique retenue.

## 35. API Documents

```
GET /api/v1/documents/{id}
POST /api/v1/documents/generate
GET /api/v1/document-templates
POST /api/v1/documents/{id}/download
```

- Permission.
- Lien source.
- Document versionné.
- Accès sécurisé.

## 36. API Notifications

```
GET /api/v1/notifications
POST /api/v1/notifications/{id}/read
GET /api/v1/notification-preferences
```

## 37. Pagination et filtres

- page/pageSize ou curseur.
- sort.
- filter.
- search.
- dateFrom/dateTo.
- companyId seulement comme filtre autorisé, jamais comme bypass de scope.
- Limites de page.

## 38. Recherche

Les recherches globales doivent être agrégées par domaine et filtrées selon les permissions. Une
recherche ne doit jamais révéler l'existence d'une ressource à laquelle l'utilisateur n'a pas
accès.

## 39. Transactions et Unit of Work

- Application service contrôle la transaction.
- Une opération critique doit avoir une frontière transactionnelle claire.
- Éviter les transactions interminables.
- Les appels externes ne doivent pas être enfermés dans une transaction DB longue lorsque cela
  peut être évité.
- Outbox pour événements fiables.

## 40. Outbox

```
BUSINESS TRANSACTION
      ↓
DB TRANSACTION
 ├── BUSINESS DATA
 └── OUTBOX EVENT
      ↓
WORKER
      ↓
EXTERNAL SYSTEM / NOTIFICATION / INTEGRATION
```

## 41. Gestion des événements

- Événement métier interne.
- Outbox.
- Consommation idempotente.
- Retry.
- Dead-letter lorsque nécessaire.
- Audit.
- Monitoring.

## 42. Performance

- Asynchronous pour traitements lourds.
- Pagination obligatoire sur listes volumineuses.
- Projections/queries dédiées pour reporting.
- N+1 queries interdites sur endpoints critiques.
- Timeouts.
- Cancellation tokens.
- Compression lorsque pertinent.

## 43. Rate limiting

- Par utilisateur.
- Par IP lorsque pertinent.
- Par endpoint.
- Plus strict pour login/webhooks publics.
- Limites adaptées aux POS et intégrations.
- Réponse claire en cas de dépassement.

## 44. Sécurité API

- HTTPS.
- Secrets hors code.
- Validation.
- Authorization.
- CORS strict.
- Protection injection.
- Rate limiting.
- Headers.
- Gestion fichiers.
- Audit.
- Logs sans secrets.

## 45. OpenAPI

- Documentation OpenAPI/Swagger.
- Schemas DTO.
- Descriptions.
- Codes erreur.
- Exemples.
- Authentification documentée.
- Version API visible.
- Utilisable par frontend et intégrateurs.

## 46. Tests API

- Tests endpoint.
- Tests authorization.
- Tests multi-tenant.
- Tests validation.
- Tests idempotence.
- Tests transactions.
- Tests concurrence.
- Tests webhook.
- Tests offline.
- Tests performance.

## 47. Observabilité API

- Request ID.
- Correlation ID.
- Durée.
- Statut HTTP.
- Endpoint.
- Utilisateur pseudonymisé/identifiant interne selon logs.
- Entreprise/scope.
- Erreurs.
- Métriques par module.

## 48. Documentation développeur

- Conventions endpoints.
- DTO.
- Erreurs.
- Auth.
- Permissions.
- Idempotence.
- Pagination.
- Webhooks.
- Offline sync.
- Versioning.
- Exemples.

## 49. Scénarios critiques

- Caissier crée vente.
- Vente à crédit.
- Achat à crédit.
- Paiement.
- Remboursement.
- Ajustement stock.
- Comptable crée écriture.
- Comptable clôture période.
- Comptable en chef consulte deux entreprises.
- Direction consulte groupe.
- Consolidation.
- Webhook paiement doublé.
- Transaction offline synchronisée.
- Utilisateur tente accès entreprise interdite.

## 50. Critères de sortie

- Architecture API définie.
- Modules définis.
- Versionnement défini.
- Auth définie.
- Scopes définis.
- Validation définie.
- Erreurs définies.
- Idempotence définie.
- Endpoints métier principaux définis.
- Finance définie.
- Comptabilité définie.
- Consolidation définie.
- Offline défini.
- Webhooks définis.
- Outbox définie.
- Sécurité définie.
- OpenAPI définie.
- Tests définis.
- Observabilité définie.

## 51. Étape suivante

Passer à la **Phase 19 — Architecture Frontend React/TypeScript & POS**. Elle détaillera
l'application web, l'organisation des features, le state management, le routing, le design system
technique, les écrans par rôle, le POS tactile et le client offline.

---

**NEXORA V1.0 — PHASE 18 VALIDÉE → PHASE 19 : FRONTEND REACT / TYPESCRIPT & POS**
