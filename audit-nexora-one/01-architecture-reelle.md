# 01 — Architecture réelle

> Observée dans le code de HEAD `e5e243d` (branche `develop`), pas dans la documentation. Volumétrie : lignes des
> fichiers suivis par Git.

## 1. Composants

```
Navigateur ── React 19 / Vite 6 (SPA, service worker, file hors ligne chiffrée)
   │  HTTPS (Caddy : TLS, HSTS, CSP ; /api/* → API ; sondes et /metrics bloqués par chemin exact)
   ▼
ASP.NET Core 10 — Nexora.Api (74 contrôleurs, 1 029 points d'accès, middlewares, planificateur hébergé)
   │  Application (services métier, moteur financier) ── Contracts (DTO) ── Domain (entités)
   ▼
Nexora.Infrastructure (EF Core 10 « base d'abord » + SQL brut paramétré, intercepteur RLS, fournisseurs externes)
   │  rôle nexora_app (sans BYPASSRLS) ; variables de session nexora.company_ids / group_ids / user_id / bypass_rls
   ▼
PostgreSQL 18 — schéma nexora : 291 tables, RLS forcée sur 250, 476 déclencheurs, 79 fonctions, 99 migrations
   (embarquées dans l'API : assistant d'installation et commande `migrate`)

Fournisseurs externes : MonCash (HTTP), SMTP (MailKit), Twilio, Firebase (FCM), webhooks sortants (SSRF filtré)
Exploitation : docker-compose prod (postgres, migrate, api, web/Caddy, sauvegarde), OTel/Prometheus/Jaeger en option
CI GitHub Actions : base, backend (build, tests, migrate --status, scan), frontend, images, E2E Playwright
```

| Composant | Fichiers | Lignes | Rôle observé |
|---|---|---|---|
| `Nexora.Api` | 98 | 9 223 | Hôte, contrôleurs minces, sécurité (JWT, permissions, step-up, idempotence), middlewares, CLI `migrate`/`bootstrap`, planificateur |
| `Nexora.Application` | 86 | 10 899 | Services d'orchestration : ventes, retours, caisse, stock, achats, moteur financier, règles de comptabilisation, calcul de paie |
| `Nexora.Contracts` | 51 | 4 368 | DTO d'API |
| `Nexora.Domain` | 25 | 1 571 | Entités et invariants simples (Money, Result, écriture en brouillon) |
| `Nexora.Infrastructure` | 180 | 57 024 | **L'essentiel de la logique métier** : services SQL par module, magasins EF, fournisseurs externes |
| Tests backend | 105 | 22 009 | Unitaires, intégration (clone de la base modèle), API (WebApplicationFactory) |
| `database/` | 113 | 16 696 | Migrations V001-V099, jeux de démonstration et de recette, scripts |
| Frontend `src/` | 264 | 42 381 | 40 modules d'écrans, client API, hors ligne, design system |
| `e2e/` | 10 | 1 102 | Playwright (6 scénarios) |
| `infrastructure/`, `.github/` | 13 | 827 | Compose, Caddy, observabilité, CI |
| `docs/` | 131 | 41 235 | Documentation (non probante) |

## 2. Chaînes d'appel types

- **Vente** : `PosController` → `SalesService.CheckoutAsync` (une transaction) → `ISalesPricing` (prix, promotions,
  coupon) → `IStockLedger` (mouvements ; déclencheurs de solde) → `FinancialEngine.HandleAsync(SaleCompleted)` →
  `PostingRuleSet` (écriture) + sous-registres (créance) + `MoveCashAsync` (transaction de trésorerie,
  `AdjustBalanceAsync`) → `cash_movement` de session.
- **Lecture** : contrôleur → service SQL de l'infrastructure (requête paramétrée) → RLS.
- **Hors ligne** : file chiffrée navigateur → `POST /sync` → `SyncServices` (idempotence par `operation_uuid`) → mêmes
  services que la caisse en ligne.

## 3. Architecture présente vs architecture visée

| Visé (documentation, nommage) | Observé | Écart |
|---|---|---|
| Clean Architecture : règles métier dans Domain/Application | Domain réduit (1 571 lignes) ; la majorité des règles vit dans des services SQL de l'infrastructure (57 024 lignes) | Logique dispersée, peu testable sans base ; testée surtout par intégration |
| Validation des entrées par FluentValidation | Paquet référencé, **aucun validateur** ; validations écrites à la main dans les services | Dépendance inutilisée |
| Isolation multi-entreprise par RLS | RLS réelle et forcée ; mais portée système fréquente et posée seulement à l'ouverture de connexion | Défense en profondeur partielle (A-009) ; contournements (A-031, A-032) |
| Une seule source de prix (« calcul unique », phase 1) | Moteur explicable (simulateur, listes de prix) ≠ tarification caisse (`SqlSalesPricing`, sans listes) | Deux calculs (A-013) |
| Trésorerie = Σ transactions | Solde stocké mis à jour en lecture-modification-écriture | Dérive sous concurrence (A-018) |
| Caisse ↔ trésorerie ↔ comptabilité reliées | Ventes et versements reliés ; retours espèces, apports/sorties non reliés à la session ou à la comptabilité | A-002, A-019 |
| Plateforme SaaS multi-clients | Console d'exploitation de portée plateforme ouverte à des rôles d'entreprise | A-031 |
| Idempotence des écritures | `operation_uuid` dans le moteur et la synchro ; filtre `Idempotency-Key` (53 points d'accès) en cache mémoire | Non partagé entre instances, sans verrou |

## 4. Peut-on poursuivre sans refonte ?

Oui. Les défauts relevés sont localisés (une requête, un service, une politique, une permission) et l'ossature tient :
transaction unique par opération métier, moteur en partie double équilibré sur 2 580 écritures, RLS forcée, sécurité des
sessions solide. Deux chantiers transverses sont nécessaires sans être des refontes : (1) le modèle de portée système et
de rôle de plateforme (A-009, A-031), (2) la tenue des soldes et la liaison caisse ↔ trésorerie ↔ comptabilité (A-002,
A-018, A-019, A-023). La concentration de la logique dans des services SQL rend chaque correction dépendante de tests
d'intégration, d'où l'importance de faire tourner ces tests avec le rôle réel (T-001).
