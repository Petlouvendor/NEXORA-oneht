# 01 — Architecture réelle (édition 2)

> Observée dans le code de HEAD `6a47c6d` (branche `develop`), pas dans la documentation. Volumétrie : lignes des
> fichiers suivis par Git. Objets de base comptés par SELECT en lecture seule sur `nexora_recette` (2026-10-17).
> L'ossature n'a pas changé depuis l'édition 1 (`e5e243d`) ; seuls les chiffres et les écarts sont mis à jour.

## 1. Composants

```text
Navigateur ── React 19 / Vite 6 (SPA, service worker, file hors ligne chiffrée)
   │  HTTPS (Caddy : TLS, HSTS, CSP ; /api/* → API ; sondes et /metrics bloqués, variantes comprises — A-024 corrigé)
   ▼
ASP.NET Core 10 — Nexora.Api (80 contrôleurs, 1 032 points d'accès, middlewares, planificateur hébergé)
   │  Application (services métier, moteur financier) ── Contracts (DTO) ── Domain (entités)
   ▼
Nexora.Infrastructure (EF Core 10 « base d'abord » + SQL brut paramétré, intercepteur RLS, fournisseurs externes)
   │  rôle nexora_app (sans BYPASSRLS) ; variables de session nexora.company_ids / group_ids / user_id / bypass_rls
   ▼
PostgreSQL 18 — schéma nexora : 291 tables, RLS forcée sur 251, 476 déclencheurs, 79 fonctions, 102 migrations
   (embarquées dans l'API : assistant d'installation et commande `migrate`)

Fournisseurs externes : MonCash (HTTP), SMTP (MailKit), Twilio, Firebase (FCM), webhooks sortants (SSRF filtré)
Exploitation : docker-compose prod (postgres, migrate, api avec planificateur activé, web/Caddy, sauvegarde)
CI GitHub Actions : base, backend (build, tests, migrate --status, scan de vulnérabilités bloquant), frontend, images, E2E
```

| Composant | Fichiers | Lignes | Éd. 1 (lignes) | Rôle observé |
| --- | --- | --- | --- | --- |
| `Nexora.Api` | 100 | 9 350 | 9 223 | Hôte, contrôleurs minces, sécurité (JWT, permissions, step-up, idempotence, garde plateforme), CLI `migrate`/`bootstrap`/`reprise-ouvertures`, planificateur |
| `Nexora.Application` | 88 | 11 205 | 10 899 | Ventes, retours, caisse, stock, achats, moteur financier, règles de comptabilisation, paie |
| `Nexora.Contracts` | 52 | 4 388 | 4 368 | DTO d'API |
| `Nexora.Domain` | 26 | 1 601 | 1 571 | Entités et invariants simples |
| `Nexora.Infrastructure` | 186 | 57 688 | 57 024 | **L'essentiel de la logique métier** : services SQL par module, magasins EF, fournisseurs |
| Tests backend | 122 | 23 841 | 22 009 | Unitaires (103), intégration (532, clone de la base modèle), API (200) |
| `database/` | 116 | 16 789 | 16 696 | Migrations V001-V102, jeux de démonstration et de recette, scripts |
| Frontend `src/` | 266 | 42 533 | 42 381 | 36 dossiers de modules, client API, hors ligne, design system |
| `e2e/` | 10 | 1 102 | 1 102 | Playwright (6 scénarios) |
| `infrastructure/`, `.github/` | 13 | 834 | 827 | Compose, Caddy, observabilité, CI |
| `docs/` | 132 | 41 797 | 41 235 | Documentation (non probante) |

## 2. Chaînes d'appel types

- **Vente** : `PosController` → `SalesService.CheckoutAsync` (une transaction ; date = horloge serveur) →
  `ISalesPricing` → `IStockLedger` → `FinancialEngine.HandleAsync(SaleCompleted)` → `PostingRuleSet` + sous-registres +
  `MoveCashAsync` (mise à jour atomique du solde, V100) → `cash_movement` de session.
  Défaut : le coût des ventes est passé en devise de vente puis converti une seconde fois (**A-052**).
- **Lecture** : contrôleur → service SQL de l'infrastructure (requête paramétrée) → RLS (contexte réappliqué après
  une portée système).
- **Hors ligne** : file chiffrée → `POST /sync/batch` → `SyncServices` (idempotence par `operation_uuid`) → mêmes
  services que la caisse, mais avec la date et la session fournies par l'appareil (**A-003 résidu**).
- **Extourne** : `ReversalService` passe l'origine en `reversed` et crée le miroir `posted` ; les agrégats ne lisent que
  `posted` (**A-043**).

## 3. Architecture présente vs architecture visée

| Visé | Observé (éd. 2) | Écart |
| --- | --- | --- |
| Clean Architecture | Domain réduit ; règles dans des services SQL de l'infrastructure | inchangé ; testable surtout par intégration |
| Validation par FluentValidation | paquet référencé, aucun validateur | inchangé |
| Isolation par RLS | RLS forcée ; contexte réappliqué (A-009 corrigé) ; 3 tables d'identité sans RLS | résidu A-009 |
| Calcul de prix unique | caisse et aperçu alignés ; simulateur encore différent sur promotion + coupon | A-039 |
| Trésorerie = Σ transactions | mise à jour atomique (V100) | A-018 corrigé |
| Caisse ↔ trésorerie ↔ comptabilité | reliées (A-002, A-019 corrigés) ; routes `/finance/*` en parallèle sans contrôles | A-045 |
| Plateforme SaaS | garde plateforme (A-031 corrigé) ; webhook de facturation à jeton par défaut | A-049 |
| Cycle comptable pluriannuel | exercices et périodes créés seulement à l'amorçage | **A-042** |
| Multidevise | taux par transaction ; arrondis et coût des ventes défaillants ; KPI qui additionnent les devises | A-041, A-046, A-052 |
| Idempotence | moteur et synchro par `operation_uuid` ; filtre `Idempotency-Key` en cache mémoire | inchangé |

## 4. Peut-on poursuivre sans refonte ?

Oui. Les anomalies de l'édition 2 sont localisées : une règle de comptabilisation (A-052), un filtre de statut dans
des requêtes (A-043), une création d'exercice manquante (A-042), un contrôle de taxe (A-040), un verrou (A-038), une
garde de démarrage (A-049), des permissions (A-033, A-034, A-045, A-048). Les chantiers transverses de l'édition 1
(portée système, tenue des soldes, liaison caisse ↔ comptabilité) ont été traités. Reste transverse : la cohérence
multidevise (A-041, A-046, A-052), à couvrir par des tests à taux non entier.
