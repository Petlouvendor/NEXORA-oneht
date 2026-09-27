# NEXORA — Phase 19 — Frontend React/TypeScript & POS : état & validation

Référence : [`phase-19-frontend-react-typescript-pos.md`](phase-19-frontend-react-typescript-pos.md)
· [`phase-13-implementation.md`](phase-13-implementation.md) (design system) ·
[`design-system.md`](design-system.md).

Le frontend **existe déjà** : `frontend/nexora-web/` — React 19 + TypeScript strict + Vite 6,
React Router 7, `shared/ui` (bibliothèque de composants + états), `core/apiClient` centralisé,
`ProtectedRoute` + `hasPermission`, `PreferencesContext` (thème / densité / langue), coquille
premium + palette Ctrl+K, écrans Dashboard / POS / Intégrations / Sécurité / Sync / Admin /
Secteurs / Groupe. Cette phase valide la conformité au document de référence et **livre la file
d'attente hors ligne du POS** (§26, §41-42) — le grand manque restant.

## Structure du projet (§4-5)

| Modèle | Réel |
|---|---|
| `app/{router,providers,permissions,config}` | ✅ `app/router.tsx`, `app/nav.ts`, `app/CommandPalette.tsx`, `app/AppLayout.tsx` ; providers dans `main.tsx` (`AuthProvider` → `PreferencesProvider` → `ToastProvider`) ; permissions via `auth/AuthContext.hasPermission` + `auth/ProtectedRoute` |
| `features/<domaine>/` | ✅ un dossier par domaine : `auth`, `dashboard`, `pos`, `sync`, `security`, `integrations`, `admin`, `sectors`, `group`, `settings`, `catalog`, `inventory`, `finance`, `accounting`, `reports`, `purchasing`, `sales`, `notifications`, `organization` (chacun avec sa page + son `*Api.ts` + ses types) |
| `components/` | ✅ = `shared/ui.tsx` (Button, Field/TextInput, Select, Card, Badge, StatePill, Money, KpiCard, Tabs, Modal, Drawer, ConfirmDialog, EmptyState, Skeleton, Spinner, Tooltip, HelpTip, DataTable) + `shared/icons.tsx` + `shared/Toast.tsx` |
| `layouts/` | ✅ `AppLayout` (application), `LoginPage` (auth) ; POS = `PosPage` en pleine page |
| `services/` | ✅ `core/apiClient.ts` + les `*Api.ts` par feature |
| `stores/` | ✅ `shared/PreferencesContext`, `auth/AuthContext`, `shared/offlineQueue` (outbox POS) — **pas de store global monolithique** (§12) |
| `hooks/` | ✅ `shared/useOnline`, `shared/useOutbox`, `shared/ui:useClickOutside` |
| `utils/` / `styles/` / `types/` | ✅ `shared/format.ts`, `shared/i18n.ts` ; `shared/theme.css` ; types co-localisés par feature |

## Correspondance spec → implémentation

| § | Exigence | État |
|---|---|---|
| 2 | Stack TS + React + Router + client HTTP typé + store local POS + design system + tests | ✅ + **Vitest** ajouté cette phase (`npm test`) |
| 3 | Serveur = autorité ; pas de contournement des permissions ; **offline ne simule jamais une confirmation** ; a11y dès le départ | ✅ — le POS hors ligne met en **file d'attente**, jamais « confirmé » (§26) ; `hasPermission` masque l'UI **et** l'API refuse (RLS + `[RequirePermission]`) |
| 6 | Layouts | ✅ application / auth ; POS plein écran ; reporting/admin réutilisent `AppLayout` |
| 7-8 | Navigation par permission, contexte entreprise permanent, recherche globale, favoris, raccourcis | ✅ `nav.ts` groupé + filtré par permission, barre de contexte (groupe → entreprise), **Ctrl/⌘+K**, favoris épinglés, badge En ligne / Hors ligne / **N en file** |
| 8 | Changement de contexte → rafraîchit les données, aucune donnée de l'ancien contexte visible | ✅ `key={location.pathname}` sur `<main>` + `useEffect([…, activeCompanyId])` dans les pages ; `AuthContext.setActiveCompany` persiste + relit `/me` |
| 9 | Permissions depuis le backend, guards route + action, masquage, jamais suffisant seul, 401/403 | ✅ `/me` renvoie les permissions ; `ProtectedRoute` (route) + `hasPermission` (action) ; `apiClient` : **401 → `session_expired` + `onUnauthorized` (logout)**, **403 → `ApiError.isForbidden`** |
| 10 | Login, MFA, session, logout, expiration, réauth action sensible, écran session expirée | ✅ login + step-up (`security/step-up`) + logout + `security_epoch` (déconnexion globale). MFA UI et écran « session expirée » dédié : à compléter (aujourd'hui redirection login). |
| 11 | Client API centralisé, headers auth, correlation ID, idempotency key, gestion erreurs, retry sûr, cancellation, parsing DTO, 401/403 | ✅ **`core/apiClient.ts` étendu cette phase** : option `idempotencyKey` → en-tête `Idempotency-Key` (§12) ; correlation ID auto ; **erreur réseau → `ApiError { status: 0 }` + `isOffline`** ; `AbortSignal` supporté via `RequestInit` |
| 12 | État serveur ≠ UI ≠ session ; panier POS local ; **offline queue persistante** | ✅ panier = état local du composant ; session = `AuthContext` ; **outbox = `shared/offlineQueue.ts`** (localStorage, versionné, listeners) |
| 13-16 | Dashboards (direction / comptable / manager) | ✅ `DashboardPage` (Command Center) + `dashboard/DashboardPage` par preset + vue résumé/détaillée + onboarding ; endpoints `dashboard/manager`, `dashboard/cashier`, `dashboard/groups` existent (Phase 7) — écrans par rôle dédiés à décliner |
| 17-25 | POS : écran principal, recherche, panier, paiement, crédit, vente suspendue, retours, clôture caisse | ✅ layout §13-14/§18, montant dû géant, rendu monnaie, **reste à crédit identifié**, `ConfirmDialog` avant la vente ; endpoints `pos/{checkout,park,returns,sessions/*}` câblés (`posApi`). Recherche produit tolérante / favoris / scanner matériel : à enrichir. |
| 26, 42 | **POS offline** : badge, compteur, état par opération, sync auto, retry, résolution conflits, **aucune double vente** | ✅ **livré cette phase** — voir ci-dessous |
| 27-34 | Écrans Stock / Achats / Clients / Fournisseurs / Finance / Comptabilité / Consolidation / Administration | ⚙️ `admin` (Phase 14), `group` (Phase 9), `sectors` (Phase 8), `integrations` (Phase 12), `sync` (Phase 10), `security` (Phase 11) **livrés** ; Stock / Achats / Clients / Fournisseurs / Finance / Comptabilité : services + `ModulePlaceholder`, écrans à habiller avec `shared/ui` |
| 35 | Recherche globale groupée par domaine, raccourci, permissions | ✅ palette Ctrl+K sur la navigation (respecte les permissions) ; recherche d'entités transverses à ajouter |
| 36-37 | Notifications, Documents | ⚙️ `notification` en base + relances ; centre de notifications UI + rendu PDF : à exposer |
| 38-39 | Formulaires (validation immédiate, erreur par champ, résumé avant action) ; tables (pagination, tri, colonnes configurables, virtualisation) | ✅ `Field` (label / requis / erreur), `ConfirmDialog` (résumé d'impact) ; `DataTable` (colonnes numériques, totaux, état vide). Virtualisation gros volumes : à ajouter. |
| 40 | Performance : code splitting, lazy, debounce, virtualisation, préchargement POS, budgets | ⚙️ bundle unique ~390 kB / 119 kB gz ; `React.lazy` par route + `Suspense` + virtualisation **à activer** ; `restoreMocks` + tests rapides |
| 41 | Offline storage : IndexedDB, données minimales, chiffrement, outbox, version de schéma, migration | ⚙️ **outbox livré sur `localStorage`** (versionné `SCHEMA_VERSION`, migration = reset si version différente) ; passage à IndexedDB chiffré **sans changer l'API** de `offlineQueue` |
| 43 | Périphériques POS (scanner, imprimante, tiroir, balance, afficheur, TPE) + abstraction + statut | ⚙️ scanner = saisie clavier (`Entrée`) ; abstraction matériel `integration_connector` type `pos_terminal` (Phase 12) ; pilotes navigateur (WebUSB / WebHID / impression) **non livrés** |
| 44-45 | Responsive + accessibilité | ✅ sidebar → tiroir < 900 px, tableaux à défilement propre ; focus-visible, lien d'évitement, `aria-live`, cibles 40 px, couleur + icône, `prefers-reduced-motion` |
| 46-47 | Design system + 11 états d'interface | ✅ `shared/ui` + `theme.css` tokens ; états Initial/Loading/Success/Empty/Error/Offline/Pending/Conflict/PermissionDenied/ReadOnly couverts (`StatePill` intègre `synced/syncing/conflict`) |
| 48 | Gestion des erreurs (réseau, métier, permission, session, conflit, timeout, paiement pending, intégration) — message clair, détails en diagnostic | ✅ `ApiError` (`isOffline`, `isForbidden`) + toasts + `nx-error-text` ; `correlationId` conservé pour le support |
| 49 | Sécurité front : pas de secrets, pas d'autz uniquement client, XSS (React), tokens conformes backend, auto-lock POS | ✅ aucun secret dans le bundle (clé JWT côté serveur) ; React échappe par défaut ; token en `localStorage` (arbitrage assumé, jeton court ≤ 60 min). Auto-lock POS : à ajouter. |
| 50-52 | Tests frontend + tests POS critiques + tests UX par rôle | ⚙️ **Vitest + 11 tests** livrés (`apiClient`, `offlineQueue`) ; tests de composants / hooks / routing / E2E navigateur / a11y : Phase 15 |
| 53 | PWA / installation locale | ⚙️ non ; service worker + manifest + versioning assets à ajouter si retenu |
| 54 | i18n | ✅ `shared/i18n.ts` (FR + architecture prête) + `shared/format.ts` (monnaie / date / nombre par locale) |
| 55 | Analytics produit | ⚙️ non (respect vie privée — à cadrer) |

## Ce qui est livré dans cette phase

- `docs/phase-19-frontend-react-typescript-pos.md` — spec verbatim.
- **`core/apiClient.ts`** (§11-12, §48) : option `idempotencyKey` → en-tête `Idempotency-Key` ;
  `crypto.randomUUID` avec repli ; **erreur réseau → `ApiError { status: 0 }`** avec accesseurs
  `isOffline` / `isForbidden` ; message 401 = `session_expired`.
- **`shared/offlineQueue.ts`** (§26, §41-42) — file d'attente hors ligne du POS :
  - `enqueue` **idempotent** (un même `id` = operation UUID n'est jamais dupliqué → **aucune
    double vente**) ; persistance `localStorage` versionnée ; `subscribeOutbox` (listeners) ;
  - `flushOutbox(sender)` — envoie les opérations `pending`/`failed` via un **lot de
    synchronisation serveur** ; met à jour le statut par opération (`synced` / `conflict` /
    `failed` / retour à `pending` si le lot ne part pas) ; ne supprime rien (purge explicite
    `pruneSynced`) ; back-off implicite via `MAX_ATTEMPTS = 8` ;
  - `autoFlushOnReconnect(sender)` — rejoue à l'événement `online`.
- **`shared/useOutbox.ts`** — hook `useSyncExternalStore` (snapshot stable) → `{ entries, pending, hasConflict }`.
- **`sync/syncApi.ts`** — `pushBatch(deviceId, operations)` → `POST /api/v1/sync/batch` (Phase 10).
- **`pos/PosPage.tsx`** — sur `checkout` en échec réseau : `enqueue` + toast « vente mise en file »
  (panier vidé, **aucun reçu**) ; panneau « File d'attente » (§26) avec statut par opération +
  bouton **Synchroniser** ; champ de config `syncDeviceId` ; badge « N en file » ; auto-flush.
- **`app/AppLayout.tsx`** — le badge de connectivité affiche « · N en file ».
- **`shared/ui.tsx`** — `StatePill` connaît `synced` / `syncing` / `conflict`.
- **Vitest** — `vite.config.ts` (bloc `test`, environnement jsdom), `package.json` (`npm test`),
  `.github/workflows/ci.yml` (étape `npm test`).
- `docs/phase-19-implementation.md` — ce document.

## Vérifications exécutées

**Tests frontend `npm test` — 11/11** :

| Fichier | Vérifié |
|---|---|
| `core/apiClient.test.ts` (4) | en-têtes bearer / `X-Company-Id` / correlation / `Idempotency-Key` ; **401 → `onUnauthorized` + `ApiError session_expired`** ; **erreur réseau → `status 0` + `isOffline`** ; RFC 7807 (title/detail/correlationId conservés) + **403 → `isForbidden`** |
| `shared/offlineQueue.test.ts` (7) | mise en file + persistance versionnée ; **idempotent (id dupliqué → une seule entrée)** ; notification des abonnés ; flush → `synced` ; **conflit serveur → statut `conflict`, rien d'écrasé** ; lot qui ne part pas → retour `pending` + `attempts++` ; **une opération `synced` n'est jamais rejouée** |

**Frontend** : `tsc` strict + `vite build` OK (78 modules). **Backend inchangé : 125 tests verts**,
Release 0 warning. DB inchangée (34 migrations, 161 tables). `smoke_test` + démo OK.

## Manques identifiés (Phase 15 / itérations)

| Sujet | Manque |
|---|---|
| Écrans métier (§27-34) | Stock / Achats / Clients / Fournisseurs / Finance / Comptabilité à habiller avec `shared/ui` |
| Offline (§41) | passage IndexedDB chiffré (API `offlineQueue` inchangée) ; écran de résolution de conflits dans le POS |
| Périphériques (§43) | pilotes WebUSB / WebHID / impression, statut périphérique |
| Performance (§40) | `React.lazy` par route, virtualisation des tables volumineuses, budgets de bundle |
| Auth (§10) | écran « session expirée » dédié, MFA UI, auto-lock POS |
| PWA (§53) | service worker + manifest + versioning assets |
| Tests (§50-52) | composants / hooks / routing / E2E navigateur / a11y / tests UX par rôle |
| Notifications / Documents (§36-37) | centre de notifications UI, prévisualisation / génération de documents |

## Couverture des critères de sortie (§56)

Structure / features / routing / permissions / client API / state management / POS / **offline** /
design system / accessibilité / i18n : **✅ définis et en production**. Finance / Comptabilité /
Consolidation / Administration : ⚙️ services + certains écrans livrés, autres à habiller.
Performance / périphériques : ⚙️ définis, optimisations à activer.

## Suite — Phase 20

Backlog de développement & découpage en sprints : transformer les architectures PostgreSQL /
.NET / React en lots de travail ordonnés (priorités, dépendances, critères d'acceptation,
séquence). NEXORA a déjà été construit phase par phase (2–14) puis spécifié (16–19) ; la Phase 20
produira le plan de finition V1.0.
