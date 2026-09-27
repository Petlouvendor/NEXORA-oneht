# NEXORA — PHASE 16 — ARCHITECTURE TECHNIQUE

**Architecture de référence pour la réalisation de NEXORA V1.0**
Version 1.0

## 1. Objectif

Transformer les 15 phases fonctionnelles en une architecture technique cohérente, évolutive et
exploitable. Cette architecture doit permettre à NEXORA de fonctionner en mono-entreprise ou
multi-entreprises, local, offline, hybride ou cloud, tout en conservant un même cœur métier.

## 2. Stack de référence

- Backend : C# avec .NET / ASP.NET Core.
- Frontend : TypeScript avec React.
- Base de données : PostgreSQL.
- API : REST versionnée.
- Temps réel : WebSocket/SignalR lorsque nécessaire.
- Authentification : mécanisme moderne basé sur tokens/session sécurisée selon déploiement.
- Conteneurisation : Docker recommandée pour les environnements serveur.
- CI/CD : pipeline automatisé.
- Observabilité : logs structurés, métriques et traces.

Cette architecture est une base de référence. Les choix précis de bibliothèques et services
seront arrêtés pendant la conception détaillée et les prototypes.

## 3. Architecture générale

```
┌──────────────────────────────────────────────┐
│                NEXORA CLIENT                 │
│ React + TypeScript + POS                     │
└──────────────────────┬───────────────────────┘
                       │ HTTPS / API
┌──────────────────────▼───────────────────────┐
│              NEXORA APPLICATION              │
│ ASP.NET Core                                  │
│ Auth • Permissions • Modules • Workflows     │
└──────────┬──────────────┬──────────────┬─────┘
           │              │              │
     ┌─────▼─────┐  ┌────▼─────┐  ┌────▼──────┐
     │ PostgreSQL│  │ Cache/Jobs│  │ Integrations│
     └───────────┘  └───────────┘  └────────────┘
```

## 4. Architecture modulaire

Le backend doit être organisé par domaines métier plutôt que comme une application monolithique
désordonnée. Un monolithe modulaire est recommandé pour V1.0 : il réduit la complexité
opérationnelle tout en préparant une séparation future de certains services.

```
NEXORA
├── Identity & Access
├── Organization
├── Catalog
├── Inventory
├── Sales / POS
├── Purchases
├── Customers
├── Suppliers
├── Payments
├── Finance
├── Accounting
├── Reporting
├── Consolidation
├── Workflow / Automation
├── Integrations
├── Notifications
├── Audit / Security
├── Documents
└── Synchronization / Offline
```

## 5. Pourquoi un monolithe modulaire V1

- Déploiement plus simple.
- Transactions métier plus faciles à garantir.
- Moins de latence inter-services.
- Plus simple pour une petite entreprise locale.
- Une seule base transactionnelle centrale.
- Évolution possible vers services séparés pour les domaines qui le justifient.

## 6. Architecture en couches

```
API
 ↓
APPLICATION
 ↓
DOMAIN
 ↓
INFRASTRUCTURE
 ↓
POSTGRESQL / SERVICES EXTERNES
```

- API : HTTP, validation d'entrée, authentification et mapping.
- Application : cas d'utilisation et orchestration.
- Domain : règles métier centrales.
- Infrastructure : PostgreSQL, fichiers, intégrations, messaging.
- Les règles financières ne doivent pas être enfouies dans les contrôleurs.

## 7. Domaines métier

- Identity : utilisateurs, sessions, MFA, appareils.
- Organization : groupes, entreprises, établissements, caisses.
- Catalog : produits, catégories, prix, taxes.
- Inventory : stocks, lots, mouvements, inventaires.
- Sales : ventes, retours, remises, crédits.
- Purchasing : commandes, réceptions, factures fournisseurs.
- Finance : caisses, banques, créances, dettes, budgets.
- Accounting : journaux, comptes, écritures, périodes.
- Consolidation : mapping, intercompany, éliminations.
- Integrations : fournisseurs externes et périphériques.
- Audit : événements et traçabilité.

## 8. Principe multi-tenant

L'entreprise est le périmètre de sécurité fondamental. Les données partagées au niveau groupe
doivent être explicitement identifiées. Toute requête métier doit être exécutée dans un contexte
d'accès contrôlé.

```
USER → SCOPE → COMPANY → RESOURCE
```

- CompanyId sur les entités concernées.
- Scope vérifié côté serveur.
- Aucun accès direct basé uniquement sur l'ID fourni par le client.
- Filtres systématiques selon contexte.
- Tests d'isolation automatisés.

## 9. Hiérarchie organisationnelle

```
GROUP → COMPANY → ESTABLISHMENT → LOCATION → TERMINAL / CASH REGISTER
```

- Le modèle doit supporter une entreprise indépendante sans groupe.
- Une entreprise peut avoir plusieurs sites.
- Un site peut avoir plusieurs caisses.
- Un utilisateur peut avoir plusieurs affectations.

## 10. Modèle de données — principes

- Clés primaires stables.
- Identifiants techniques distincts des numéros métier lorsque nécessaire.
- Dates UTC stockées côté serveur.
- Devise explicitement associée aux montants.
- Montants monétaires en type décimal approprié, jamais en float/double.
- Foreign keys.
- Index adaptés aux requêtes.
- Contraintes d'intégrité.
- Soft delete uniquement lorsque justifié.
- Audit des modifications sensibles.

## 11. Montants financiers

Les calculs financiers doivent être déterministes et reproductibles.

- Décimal exact.
- Précision définie par domaine.
- Règles d'arrondi explicites.
- Devise explicite.
- Taux de change versionnés.
- Calculs centralisés dans le domaine.
- Tests de cas limites.

## 12. Comptabilité — modèle technique

```
BUSINESS TRANSACTION
       ↓
ACCOUNTING EVENT
       ↓
JOURNAL ENTRY
   ┌── DEBIT
   └── CREDIT
       ↓
LEDGER / REPORTING
```

- Une transaction métier peut générer une ou plusieurs écritures.
- Les écritures doivent équilibrer débit/crédit.
- La source métier doit être identifiable.
- Une correction comptable doit être traçable.

## 13. Stock — modèle technique

```
RECEIPT / SALE / TRANSFER / ADJUSTMENT
                  ↓
             STOCK MOVEMENT
                  ↓
              STOCK BALANCE
```

- Le mouvement est la trace fondamentale.
- Le stock disponible peut être calculé/maintenu selon stratégie choisie.
- Lots et emplacements supportés.
- Transactions concurrentes contrôlées.
- Inventaire réconciliable.

## 14. POS — architecture

```
REACT POS
  ↓
LOCAL STATE
  ↓
POS APPLICATION API
  ↓
SALES
 ├── INVENTORY
 ├── PAYMENT
 ├── FINANCE
 └── ACCOUNTING
```

- POS optimisé pour faible latence.
- Panier local.
- Validation serveur.
- Support périphériques.
- Queue offline.
- Identifiant de transaction unique.

## 15. Offline — architecture

```
POS
 ↓
LOCAL STORE
 ↓
OUTBOX / QUEUE
 ↓
SYNC ENGINE
 ↓
API
 ↓
SERVER
 ↓
INBOX / IDEMPOTENCY
 ↓
PROCESSING
```

- Outbox côté client.
- Inbox/idempotence côté serveur.
- État de synchronisation.
- Retry.
- Détection conflit.
- Accusé de réception.
- Aucun doublon financier.

## 16. Idempotence

Toutes les opérations pouvant être rejouées doivent posséder une clé d'idempotence. Elle est
obligatoire pour les ventes offline et fortement recommandée pour paiements, webhooks et
synchronisations.

```
IDEMPOTENCY KEY → UNIQUE CONSTRAINT → SINGLE EFFECT
```

## 17. Concurrence

- Transactions DB.
- Verrouillage ou contrôle optimiste selon cas.
- Contrôle des stocks.
- Numérotation concurrente sûre.
- Prévention des doubles paiements.
- Tests de concurrence.

## 18. API REST

- Versionnement /api/v1.
- Ressources métier claires.
- DTO séparés des entités DB.
- Validation.
- Pagination.
- Filtres.
- Tri.
- Recherche.
- Erreurs standardisées.
- Correlation ID.
- Idempotency-Key pour opérations concernées.

## 19. Contrats API

- Request.
- Response.
- Validation errors.
- Business errors.
- Authorization errors.
- Not found.
- Conflict.
- Rate limit.
- Server error.
- Version.

```
SUCCESS → 2xx
CLIENT ERROR → 4xx
SERVER ERROR → 5xx
CONFLICT → 409 lorsque pertinent
```

## 20. Authentification et autorisation

- Authentification centralisée.
- RBAC.
- Scopes entreprise/site/caisse.
- MFA.
- Sessions sécurisées.
- Révocation.
- Permissions côté serveur.
- Audit des actions sensibles.

## 21. Cache

- Cache uniquement des données compatibles avec le cache.
- Ne jamais servir une donnée d'une entreprise à une autre.
- Invalider après changement pertinent.
- Les soldes financiers critiques ne doivent pas dépendre d'un cache obsolète sans stratégie
  explicite.

## 22. Jobs et traitements asynchrones

- Rapports lourds.
- Exports.
- Notifications.
- Synchronisation.
- Rapprochements.
- Consolidation.
- Imports massifs.
- Tâches planifiées.

Les traitements asynchrones doivent être idempotents, observables et rejouables.

## 23. Workflow et automatisation

```
EVENT → RULES → CONDITION → ACTION → AUDIT
```

- Moteur de règles isolé.
- Actions autorisées par type.
- Version des règles.
- Dry-run/test.
- Historique.
- Retry.
- Protection contre boucle infinie.

## 24. Intégrations

- Interface commune.
- Adapter par fournisseur.
- Secrets hors code.
- Timeout.
- Retry contrôlé.
- Circuit breaker lorsque pertinent.
- Webhook signé.
- Idempotence.
- Rapprochement.
- Monitoring.

## 25. Documents

- Génération serveur.
- Templates versionnés.
- PDF.
- Impression POS.
- Stockage contrôlé.
- Identifiant document.
- Lien transaction source.
- Archivage.
- Accès par scope.

## 26. Notifications

- Email.
- SMS.
- Messagerie lorsque connectée.
- Notifications internes.
- Templates.
- Préférences.
- Queue.
- Retry.
- Historique.
- Désabonnement lorsque applicable.

## 27. Audit

```
COMMAND → AUTHORIZATION → BUSINESS ACTION → AUDIT EVENT
```

- Événement structuré.
- Avant/après lorsque pertinent.
- Utilisateur.
- Entreprise.
- Ressource.
- Résultat.
- Correlation ID.
- Protection du journal.

## 28. Observabilité

- Logs structurés.
- Metrics.
- Distributed traces lorsque utile.
- Correlation ID.
- Health checks.
- Alertes.
- Dashboard technique.
- Dashboard synchronisation.
- Dashboard paiements.

## 29. Sécurité applicative

- Validation entrée.
- Protection injections.
- Gestion secrets.
- TLS.
- Headers sécurité.
- Rate limiting.
- Protection session.
- Upload contrôlé.
- CORS strict selon architecture.
- Erreurs sans fuite d'informations sensibles.

## 30. PostgreSQL

- Schéma relationnel normalisé lorsque pertinent.
- Index.
- Contraintes.
- Transactions.
- Migrations versionnées.
- Backups.
- Réplication selon besoin de déploiement.
- Partitionnement seulement lorsque justifié par les volumes.

## 31. Migrations de base

- Migration versionnée.
- Testée avant production.
- Rollback lorsque possible.
- Backup avant migration critique.
- Compatibilité transitoire pour migrations complexes.
- Vérification post-migration.

## 32. Fichiers et documents

- Stockage séparé des données transactionnelles si volume important.
- Références en base.
- Contrôle accès.
- Hash/intégrité lorsque nécessaire.
- Sauvegarde.
- Suppression/archivage contrôlé.

## 33. Configuration

- Configuration par environnement.
- Configuration groupe.
- Configuration entreprise.
- Secrets dans coffre sécurisé lorsque disponible.
- Pas de secret dans Git.
- Versionnement des paramètres métier sensibles.
- Audit.

## 34. Déploiement local

```
NEXORA SERVER → POSTGRESQL → LAN → POS / WEB
```

- Installation simplifiée.
- Service démarrable automatiquement.
- Sauvegarde configurée.
- Mises à jour contrôlées.
- Diagnostic local.

## 35. Déploiement cloud

```
LOAD BALANCER → APP INSTANCES → POSTGRESQL → STORAGE / QUEUE
```

- TLS.
- Backups.
- Monitoring.
- Scaling horizontal lorsque nécessaire.
- Secrets centralisés.
- Déploiement automatisé.

## 36. Hybride

```
LOCAL SITE → SYNC SERVICE ↔ CLOUD CORE
```

- Service local.
- Queue.
- Synchronisation.
- Gestion conflits.
- Monitoring central.
- Continuité locale.

## 37. Architecture de fichiers du projet

```
/src
  /Nexora.Api
  /Nexora.Application
  /Nexora.Domain
  /Nexora.Infrastructure
  /Nexora.Contracts
  /Nexora.Tests
/frontend
  /nexora-web
    /src
      /features
      /components
      /pages
      /services
      /stores
      /routes
      /styles
/docs
/deployment
/scripts
```

La structure exacte pourra évoluer, mais l'organisation doit conserver une séparation claire
entre domaine, application, infrastructure et présentation.

## 38. Frontend React

- Feature-based architecture.
- Composants réutilisables.
- Design system.
- Gestion d'état adaptée au besoin.
- Client API centralisé.
- Gestion permissions.
- Routing protégé.
- POS optimisé.
- Internationalisation.
- Accessibilité.

## 39. State management

- État serveur séparé de l'état UI.
- Cache API lorsque pertinent.
- Panier POS local.
- Contexte entreprise.
- Session utilisateur.
- File offline.
- Éviter un store global inutilement complexe.

## 40. Design system technique

- Tokens.
- Composants.
- Variantes.
- États.
- Responsive.
- Mode clair/sombre.
- Accessibilité.
- Storybook ou équivalent recommandé.
- Tests visuels sur composants critiques.

## 41. Tests techniques

- Unit tests backend.
- Unit tests frontend.
- Integration tests.
- API tests.
- Database tests.
- End-to-end.
- Security tests.
- Load tests.
- Offline tests.
- Contract tests pour intégrations.

## 42. CI/CD

```
COMMIT → BUILD → TEST → SECURITY CHECK → PACKAGE → DEPLOY STAGING → APPROVAL → PRODUCTION
```

- Build reproductible.
- Tests automatiques.
- Lint/format.
- Analyse dépendances.
- Scan secrets.
- Scan vulnérabilités.
- Migration contrôlée.
- Artifacts versionnés.

## 43. Gestion des versions

- Git.
- Branches selon stratégie d'équipe.
- Pull requests.
- Code review.
- Tags releases.
- Changelog.
- Version API.
- Migration DB liée à version.

## 44. Environnements

- Development.
- Testing.
- Staging.
- Pilot.
- Production.
- Secrets séparés.
- Bases séparées.
- Credentials séparés.

## 45. Scalabilité

- Commencer simple.
- Stateless API lorsque possible.
- Sessions partagées ou tokenisées selon architecture.
- Jobs découplés.
- Index DB.
- Pagination.
- Cache sélectif.
- Object storage.
- Possibilité de plusieurs instances backend.

## 46. Résilience

- Timeouts.
- Retries limités.
- Circuit breaker.
- Queues.
- Idempotence.
- Health checks.
- Graceful shutdown.
- Backups.
- Restoration.
- Plan de reprise.

## 47. Architecture décisionnelle

- Ne pas introduire des microservices uniquement pour suivre une tendance.
- Ne pas multiplier les technologies.
- Prioriser fiabilité transactionnelle.
- Prioriser simplicité pour le mode local.
- Séparer seulement les domaines nécessitant indépendance, scaling ou cycle de déploiement
  distinct.

## 48. Décisions techniques à figer avant codage

- Version exacte .NET.
- Version React/TypeScript.
- Bibliothèques UI.
- ORM/data access.
- Authentification.
- Moteur migrations.
- Cache.
- Queue/background jobs.
- Stockage fichiers.
- Observabilité.
- CI/CD.
- Hébergement cible.
- Stratégie offline locale.
- Stratégie impression/périphériques.

## 49. Critères de sortie

- Architecture générale validée.
- Stack validée.
- Modules définis.
- Couches définies.
- Multi-tenant défini.
- Modèle financier défini.
- Comptabilité définie.
- POS défini.
- Offline défini.
- API définie.
- Sécurité définie.
- Intégrations définies.
- Audit défini.
- Observabilité définie.
- Déploiement local/cloud/hybride défini.
- Structure projet définie.
- CI/CD définie.
- Décisions techniques restantes identifiées.

## 50. Étape suivante

Passer à la **Phase 17 — Modèle de données PostgreSQL**. Cette phase détaillera les principales
tables, relations, clés, contraintes, index, périmètres multi-entreprises, données financières,
comptables, stock, POS, utilisateurs, audit, synchronisation et consolidation avant le début du
développement.

---

**NEXORA V1.0 — PHASE 16 VALIDÉE → PHASE 17 : MODÈLE DE DONNÉES POSTGRESQL**
