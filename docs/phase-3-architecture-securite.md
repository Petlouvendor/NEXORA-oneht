# NEXORA — PHASE 3 — ARCHITECTURE & SÉCURITÉ

**Architecture technique cible, sécurité, multi-tenant et gouvernance des accès**
**Version 1.0**

> Document de référence. L'implémentation est dans [`backend/`](../backend/) et [`frontend/`](../frontend/) ;
> voir [`docs/phase-3-implementation.md`](phase-3-implementation.md) pour l'état livré.

---

## 1. Objectif

Définir l'architecture technique de NEXORA à partir des phases fonctionnelle et données V1.0, avec priorité à la sécurité, la modularité, la maintenabilité, les performances et l'évolution d'une petite entreprise vers un groupe multi-entreprises.

## 2. Stack retenue

- **Backend** : C# / ASP.NET Core.
- **Frontend** : React + TypeScript.
- **Base de données** : PostgreSQL.
- **API principale** : REST/JSON.
- **Déploiement** : local, hybride ou cloud avec le même produit.
- **Conteneurisation** recommandée pour les environnements serveur.

## 3. Principes d'architecture

- Le backend est l'autorité pour les règles métier et les permissions.
- Séparation claire frontend/backend.
- Transactions financières atomiques.
- Données d'entreprise isolées par défaut.
- Configuration sans création de versions sectorielles.
- Opérations critiques idempotentes.
- Modules découplés mais partageant le noyau métier.
- Observabilité, sauvegarde et reprise prévues dès la conception.

## 4. Architecture globale

```
UTILISATEUR
   ↓
WEB / POS REACT
   ↓ HTTPS
ASP.NET CORE API
   ↓
APPLICATION / DOMAIN
   ├─ Organisation / IAM
   ├─ Commerce / POS
   ├─ Stock / Achats
   ├─ Finance / Paiements
   ├─ Comptabilité
   ├─ Reporting / Notifications
   └─ Synchronisation / Intégrations
   ↓
POSTGRESQL
   ├─ Cache éventuel
   ├─ File / événements
   └─ Stockage documents
```

## 5. Architecture backend

```
Nexora.Api
Nexora.Application
Nexora.Domain
Nexora.Infrastructure
Nexora.Contracts
Nexora.Tests
```

- **API** : endpoints, middleware, validation d'entrée, contrats.
- **Application** : cas d'utilisation et orchestration.
- **Domain** : entités, règles métier et invariants.
- **Infrastructure** : PostgreSQL, fichiers, services externes et intégrations.
- **Contracts** : DTO et contrats d'API.
- **Tests** : unitaires, intégration et scénarios métier.

## 6. Organisation des modules

Identity & Access · Organizations · Configuration · Catalog · Customers · Suppliers · Inventory · Purchasing · Sales/POS · Payments · Cash · Finance · Accounting · Reporting · Notifications · Audit · Documents · Synchronization · Integrations · Analytics/AI

Le lancement privilégie un **monolithe modulaire**. Les microservices ne seront introduits que si un besoin réel de scalabilité, isolation ou déploiement indépendant le justifie.

## 7. Architecture frontend

```
src/
├── app/
├── core/
├── auth/
├── organization/
├── dashboard/
├── pos/
├── catalog/
├── inventory/
├── purchasing/
├── sales/
├── finance/
├── accounting/
├── reports/
├── notifications/
├── settings/
└── shared/
```

- Routes protégées.
- Contexte d'entreprise actif.
- Composants issus du Design System NEXORA.
- Validation UX côté client + validation obligatoire serveur.
- POS optimisé tactile et clavier.
- Gestion d'état maîtrisée et localisée.

## 8. API et contrats

```
/api/v1/auth
/api/v1/companies
/api/v1/users
/api/v1/products
/api/v1/customers
/api/v1/suppliers
/api/v1/inventory
/api/v1/purchases
/api/v1/sales
/api/v1/payments
/api/v1/cash
/api/v1/finance
/api/v1/accounting
/api/v1/reports
```

- HTTPS en réseau.
- Validation stricte.
- Pagination et filtres contrôlés.
- Erreurs structurées.
- **Idempotency-Key** pour opérations sensibles.
- **Correlation ID** pour le suivi d'une requête.

## 9. Authentification

- Mots de passe stockés uniquement sous forme de hash moderne.
- Sessions sécurisées, expiration et révocation.
- MFA pour comptes sensibles.
- Protection brute force.
- Réinitialisation sécurisée.
- Gestion des sessions et appareils.
- Déconnexion globale pour comptes privilégiés.

## 10. Autorisation

```
IDENTITÉ → RÔLE → PERMISSION → SCOPE → RESSOURCE → ACTION
```

- Contrôle côté backend sur chaque opération.
- Permissions distinctes : lecture, création, modification, validation, annulation, export, administration.
- Scopes groupe/entreprise/établissement/magasin/entrepôt/caisse.
- Règles supplémentaires par seuil ou workflow.
- **Deny by default.**

## 11. Multi-tenant et isolation

Le modèle V1.0 utilise l'entreprise comme unité principale d'isolation. Une base PostgreSQL partagée avec `company_id` est utilisée, avec **Row-Level Security PostgreSQL** activée (voir Phase 2).

- Le serveur détermine ou vérifie le contexte d'entreprise.
- Ne jamais faire confiance au `company_id` fourni par le navigateur.
- Les requêtes métier doivent appliquer le périmètre.
- Les accès groupe doivent être explicitement autorisés.
- Les opérations inter-entreprises utilisent un mécanisme dédié.

## 12. Contexte actif

```
USER
 ↓
ACTIVE GROUP
 ↓
ACTIVE COMPANY
 ↓
ACTIVE ESTABLISHMENT
 ↓
ACTIVE STORE / WAREHOUSE / POS
```

Le contexte actif facilite l'expérience utilisateur mais **ne constitue jamais à lui seul une autorisation**.

## 13. Sécurité API et application

TLS/HTTPS · validation et normalisation des entrées · CORS adapté · rate limiting des endpoints sensibles · requêtes paramétrées/ORM · protection XSS/CSRF selon le mécanisme d'authentification · contrôle des uploads · limites de taille · headers de sécurité · journalisation des événements de sécurité.

## 14. Sécurité PostgreSQL

- Compte runtime à privilèges minimaux (`nexora_app`, sans `BYPASSRLS`).
- Séparer migration et runtime.
- Pas d'accès public direct à PostgreSQL.
- Réseau DB restreint.
- Sauvegardes chiffrées.
- Connexions protégées.
- Rôles PostgreSQL séparés selon besoins.

## 15. Transactions et intégrité

```
VENTE
BEGIN
  vente + lignes
  stock
  paiement OU créance
  finance
  écriture comptable
COMMIT
```

- Rollback complet en cas d'échec critique.
- Idempotence des commandes sensibles.
- Concurrence traitée explicitement.
- Aucune modification partielle silencieuse.
- **Outbox/event pattern** recommandé pour événements asynchrones après commit.

## 16. Moteur financier et comptable

Le moteur financier est un **domaine central**. POS, Achats et autres modules l'utilisent ; ils ne réimplémentent pas les règles financières.

Créances · dettes · paiements et affectations · caisse et comptes financiers · taux de change · règles comptables · prévisions · rapprochements · consolidation.

## 17. Comptabilité

- Génération centralisée des écritures.
- Contrôle débit = crédit.
- Périodes et clôtures.
- Extournes/corrections.
- Traçabilité de la source métier.
- Conservation des écritures validées.
- Consolidation séparée des livres sources.

## 18. Gestion des secrets

Variables d'environnement ou secret manager · aucune clé dans Git · secrets séparés dev/test/prod · rotation · privilèges minimaux · accès sensibles journalisés lorsque possible.

## 19. Audit et observabilité

- Audit métier des actions sensibles.
- Logs techniques séparés des logs métier.
- Correlation ID.
- Métriques API, DB, POS et synchronisation.
- Alertes sur erreurs critiques.
- **Ne jamais journaliser mots de passe, tokens ou secrets.**

## 20. Sauvegarde et reprise

Sauvegardes PostgreSQL régulières · copies hors serveur principal · chiffrement · rétention définie · tests de restauration · objectifs RPO/RTO définis selon le mode de déploiement · sauvegarde des documents et configurations critiques.

## 21. Mode local

```
POS / WEB → API LOCALE → POSTGRESQL LOCAL
```

Installation simplifiée · accès réseau local · sauvegardes locales · périphériques POS accessibles · préparation possible de la synchronisation.

## 22. Mode cloud

```
CLIENTS → HTTPS → REVERSE PROXY → API → POSTGRESQL
```

TLS · monitoring · sauvegardes automatisées · déploiement contrôlé · scalabilité API lorsque nécessaire.

## 23. Mode hybride et offline

```
SITE LOCAL → FILE OFFLINE → SYNCHRONISATION → SERVEUR CENTRAL → GROUPE
```

Transactions locales identifiées de façon unique · queue durable · idempotence · retry contrôlé · détection des conflits · journal de synchronisation · statut visible · **aucune perte silencieuse**.

## 24. Performance

Pagination · index adaptés · transactions courtes · éviter N+1 queries · cache après mesure · traitements asynchrones pour tâches longues · isolation des rapports analytiques lourds si nécessaire · archivage selon volume.

## 25. Documents et fichiers

Métadonnées en PostgreSQL · fichiers dans stockage adapté aux volumes · validation type/taille · contrôle des accès · scan antivirus selon infrastructure · journalisation des accès sensibles.

## 26. Intégrations

Paiements · banques selon API disponible · Mobile Money selon fournisseur · e-commerce · WhatsApp/API si applicable · scanners, imprimantes, tiroirs-caisses, balances · **webhooks sécurisés**.

## 27. Environnements

```
DEV → TEST/QA → STAGING → PRODUCTION
```

Données de production interdites en développement sans anonymisation · migrations versionnées · configurations séparées · tests automatisés dans CI · déploiements reproductibles.

## 28. CI/CD

Build automatisé · analyse statique · tests unitaires · tests d'intégration · validation des migrations · scan des dépendances · déploiement contrôlé · rollback documenté · revue de code pour changements critiques.

## 29. Tests de sécurité

Autorisation par rôle et scope · escalade de privilèges · accès inter-entreprises · injection · sessions · brute force · uploads · API · secrets · sauvegarde/restauration.

## 30. Décisions architecturales V1.0

- Monolithe modulaire au lancement.
- PostgreSQL comme source transactionnelle centrale.
- Backend autoritaire pour règles et sécurité.
- Isolation stricte par entreprise.
- Offline préparé dès le modèle transactionnel.
- Événements/outbox pour traitements asynchrones lorsque nécessaire.
- Comptabilité intégrée au noyau métier.

## 31. Critères de sortie

- Architecture backend validée.
- Architecture frontend validée.
- Modules et responsabilités définis.
- API versionnée et sécurisée.
- Authentification/autorisation définies.
- Multi-tenant défini.
- Transactions et intégrité définies.
- Audit/observabilité définis.
- Sauvegarde/restauration définies.
- Local/cloud/hybride/offline définis.
- CI/CD et tests de sécurité définis.

## 32. Étape suivante

Après validation de cette phase, passer à la **Phase 4 — Moteur financier et comptable**. Elle détaillera les règles comptables, événements financiers, journaux, comptes, écritures, créances, dettes, paiements, trésorerie, devises, rapprochements et consolidation.

---

**NEXORA — Architecture & Sécurité V1.0 → Moteur financier & comptable**
