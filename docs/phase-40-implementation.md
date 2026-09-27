# Phase 40 — UX, Design System & Accessibilité — implémentation

**Statut : livré et vérifié.** Migration `V053` (3 tables + `user_preference` étendu).
8 tests d'intégration (`UxPersonalizationTests`), 2 tests API (`UxEndpointsTests`),
3 tests frontend (`personalizationApi.test.ts`).
**291 tests backend** verts (18 unit + 23 API + 250 intégration), Debug **et** Release, 0 warning.
66 tests frontend. `tsc` + `vite build` verts.

Le **design system NEXORA ONE est validé** — il a été construit en Phase 13 et étendu depuis :

- **Phase 13** — `shared/theme.css` (tokens couleurs / typo / espace / ombres / contrôles /
  transitions / breakpoints, thèmes `light` / `dark` / `system`, densité `comfortable` / `compact`,
  `data-motion` + `@media (prefers-reduced-motion)`), bibliothèque `shared/ui` (Button, Input,
  Select, Card, Modal, Tabs, Badge, Toast, Alert, Field, HelpTip, KpiCard…), `PreferencesContext`
  (thème / densité / langue / favoris appliqués avant peinture, cache `localStorage` + save serveur
  débattu), **coquille premium** (sidebar + contexte entreprise visible, breadcrumb), **palette
  Ctrl+K** ; `user_preference` + `company_branding` (`V032`, accent validé en luminance)
- **Phase 19** — architecture frontend validée (feature-based, `apiClient` idempotency-key +
  erreur réseau → `ApiError` statut 0, **file d'attente offline POS** `shared/offlineQueue`),
  **Vitest** ajouté
- **Phase 6 / 8 / 23 / 24** — écrans POS §16-18 (scanner prioritaire, panier toujours visible),
  Restaurant (tables / zones / cuisine §39), Pharmacie (recherche tolérante `unaccent`+`pg_trgm`,
  lots / péremption §40), Supermarché (PLU / produit pesé §41)
- **Phase 39** — onboarding UX (`MigrationWizardPage` : assistant + checklist + score §54)

Cette phase ajoute la **couche de personnalisation persistée** décrite aux §13, §21-22, §31-33,
§37, §50, §57.

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-3 | UX universelle petite boutique ↔ groupe ; `COMPLEXE À L'INTÉRIEUR → SIMPLE À L'EXTÉRIEUR → ACTION RAPIDE → RETOUR CLAIR → CONFIANCE` ; identité premium, hiérarchie forte, contraste élevé | **Phase 13** — coquille premium, densité, `data-*` sur `<html>` ; contexte entreprise toujours visible (§12) ; confirmations sur les actions sensibles (§25, Phase 11 double approbation) |
| 4, 50 | Design tokens (couleurs, typo, espace, rayons, ombres, contrôles, z-index, transitions, breakpoints, états) ; design system documentaire (guide, tokens, **version**) | `shared/theme.css` : 24 tokens couleur, 9 typo, 4 espace/forme, 2 élévation, 3 contrôle, 2 motion. **`GET /api/v1/design-system/version`** → `{version 1.1.0, wcagTarget "WCAG 2.2 AA", componentCount 21, themes[4], densities[2], tokenGroups[6], components[21], releasedOn}`. `docs/design-system.md` |
| 5-6 | Thèmes light / dark / system / **contraste élevé** ; personnalisation entreprise dans les limites ; couleur de marque réservée à l'identité, **jamais seule** pour un état | **NOUVEAU `[data-contrast="high"]`** dans `theme.css` (bords 2 px, encre renforcée `--nx-text-2 = --nx-text`, focus 3 px) piloté par `user_preference.high_contrast` ; l'accent d'entreprise reste borné en luminance (Phase 13 `IsValidAccent` : lum ∈ 0,16–0,92) ; les états portent **couleur + icône/forme** (`--nx-success` + ✓, `--nx-error` + ✕…) |
| 7 | Typographie : titres / sous-titres / corps / labels / KPI / monospace / **tailles adaptatives** / lisibilité caisse | Inter + `--nx-font-num` monospace ; **NOUVEAU `--nx-text-scale`** (0,85–1,50) appliqué à `:root { font-size: calc(var(--nx-fs-md) * var(--nx-text-scale)) }` — piloté par `user_preference.text_scale` (§31) |
| 8-10 | Iconographie cohérente + libellé sur actions ambiguës + tooltips ; composants (21) ; états (default / hover / focus / disabled / loading / success / warning / error / read-only / **offline**) | `shared/icons.tsx` (trait unique 1,6, 20×20) ; `shared/ui` (21 composants listés par `design-system/version`) ; `data-motion` / focus visible ; indicateur offline POS (Phase 19) |
| 11-12 | Navigation adaptée au rôle + favoris + recherche globale + breadcrumb + **raccourcis** ; contexte multi-entreprises toujours visible + sélecteur + confirmation avant action sensible | Sidebar filtrée par permission (`nav.ts`), favoris (`user_preference.favorites` Phase 13), **NOUVEAU `ui_shortcut`** (raccourcis clavier persistés §37-38), contexte entreprise dans la coquille (Phase 13) |
| 13-15 | Dashboard : KPI prioritaires, actions rapides, alertes, tâches, graphiques, **personnalisation par rôle** ; dashboard petite entreprise ; dashboard groupe (consolidé, comparaison, drill-down) | **NOUVEAU `dashboard_layout`** : disposition personnelle (`user_id`) **ou** défaut d'entreprise ciblé par rôle (`user_id NULL, role`) — `GetEffectiveAsync` : ma disposition → défaut de mon rôle → défaut d'entreprise. Command Center + dashboards par rôle : Phase 7 ; consolidation groupe : Phase 9 |
| 16-18 | POS (recherche → produit → panier → total → paiement → reçu, peu de clics, scanner prioritaire, total très visible) ; paiement (espèces / carte / mobile money / mixte / crédit, confirmation, temps réel) ; **POS offline** (indicateur, queue, synchro, conflit) | Écran POS Phases 6 / 13 / 19 (§13-14 spec Phase 6), `shared/offlineQueue` idempotent par UUID (Phase 19) — **validé** |
| 19-20 | Recherche globale (produits / clients / fournisseurs / factures / documents / transactions selon permission) ; **command palette** (recherche + créer vente / client / facture + navigation + raccourcis) | Palette Ctrl+K (Phase 13) — **validée** ; les `ui_shortcut` alimentent les actions rapides |
| 21-22 | Data tables (tri, filtres, **colonnes configurables**, recherche, pagination, export, colonnes gelées, responsive) ; filtres (date / entreprise / statut / … + **sauvegarde filtre**) | Colonnes : `user_preference.table_columns` (Phase 13) ; **NOUVEAU `saved_filter`** : filtres nommés par **clé de vue** (`sales.list`, `documents.list`…), **privés ou partagés** dans l'entreprise, un seul « par défaut » par vue |
| 23-29 | Formulaires (libellés, aide contextuelle, validation immédiate + serveur, messages actionnables) ; **messages d'erreur** (`PROBLÈME → CAUSE → CONSÉQUENCE → ACTION`, jamais masquer une erreur financière, correlation ID) ; confirmations ; undo (workflow avoir/annulation, jamais destructif pour la finance) ; feedback (toast / progression / états) ; chargement (skeleton / spinner court) ; états vides (expliquer + proposer une action) | `shared/ui` Field / HelpTip ; RFC 7807 + `X-Correlation-Id` (Phase 3) ; `Result`/`Error` métier (Phase 3) ; avoirs / contrepassation (Phases 29-30) ; `Toast` (Phase 13) — **validés** |
| 30-33 | Responsive (desktop / tablette / mobile / POS tactile) ; **accessibilité** (clavier, focus visible, lecteur d'écran, labels, ARIA, contraste, **taille texte**, cibles tactiles ≥ 40 px, **pas de dépendance à la couleur seule**) ; **WCAG 2.2 AA** ; POS accessibilité (boutons grands, contraste, focus) | Grilles fluides + `--nx-tap: 40px` (Phase 13) ; **NOUVEAU** contraste élevé + échelle de texte (voir §5-7) ; focus visible renforcé sous `[data-contrast="high"]` ; `role="alert"` sur les erreurs ; `design-system/version.wcagTarget = "WCAG 2.2 AA"` (audit automatisé/manuel = exploitation) |
| 34-35 | Internationalisation (fr / en / **créole haïtien** / langue par utilisateur / formats date-nombre / **fuseau horaire** / pluralisation) ; RTL prêt | `user_preference.locale` + `shared/i18n` + `shared/format` (Phase 13) ; **NOUVEAU `user_preference.timezone`** (§34) ; option « Kreyòl ayisyen » ajoutée au sélecteur. RTL : architecture logique-properties prête (backlog de traduction) |
| 36-38 | Personnalisation entreprise (logo, nom, accent borné, favicon, dashboard, menus) ; **personnalisation rôle** (dashboard, KPI, colonnes, filtres, raccourcis) ; personnalisation POS (favoris, disposition, touches rapides) | `company_branding` (Phase 13) + **NOUVEAU** `dashboard_layout` ciblé par rôle (`SaveCompanyDefaultAsync`, route `admin.configure_company`) + `saved_filter` + `ui_shortcut`. Disposition POS : `user_preference` (Phase 13) |
| 39-45 | Design restaurant / pharmacie / supermarché / finance / comptabilité / reporting ; **mode présentation** direction plein écran | Écrans sectoriels Phases 8 / 23 / 24 ; Finance Phase 28 ; Comptabilité Phase 29 ; Reporting / Analytics Phase 7 / 31 (KPI cards, charts, drill-down, filtres persistants) — **validés**. Mode présentation plein écran : **backlog** (un widget « fullscreen » sur le dashboard) |
| 46-49 | Mobile (consultation KPI / alertes / validation / tâches, ne pas reproduire le desktop) ; **PWA** (installation, cache contrôlé, offline limité, indicateur version) ; performance UX (first render, recherche produit prioritaire, lazy loading, virtualisation) ; micro-interactions (transitions courtes, **prefers-reduced-motion**) | Responsive + `reduced_motion` (Phase 13) ; `design-system/version` fournit l'**indicateur de version** ; lazy chunks Vite (Phase 19). PWA manifest + service worker : **backlog** |
| 50-53 | Design system documentaire (guide, tokens, version) ; Figma / handoff ; tests visuels (régression screenshot, dark/light, POS) ; tests UX (temps tâche, clics, satisfaction) | `docs/design-system.md` + **`GET /api/v1/design-system/version`** (catalogue de composants + groupes de tokens pour le handoff / le contrôle de compatibilité front). Régression screenshot (Playwright/Chromatic) + tests UX modérés : **exploitation** |
| 54-56 | Onboarding UX (`BIENVENUE → ENTREPRISE → CONFIG GUIDÉE → IMPORT → TEST → PREMIÈRE VENTE → PREMIER RAPPORT`) ; aide intégrée (tooltips, guides, checklist, centre d'aide) ; **sécurité UX** (jamais de secrets, masquer les données sensibles, session expirée expliquée, permissions refusées expliquées sans révéler d'info) | `MigrationWizardPage` + checklist d'onboarding (Phase 39 §31-54) ; `HelpTip` (Phase 13) ; `apiClient` 401 → « Session expirée — reconnectez-vous » (Phase 19) ; refus de permission → 403 générique (Phase 3). Centre d'aide / guides par secteur : **backlog** |
| 57 | API / préférences | **`GET/PUT /api/v1/user-preferences`** + **`/ui-preferences`** (alias de `me/preferences`, + `high_contrast` / `text_scale` / `timezone`) ; **`GET/PUT /api/v1/company-branding`** (alias) ; **`GET /api/v1/dashboard-layouts`** (`{effective, all}`) + **`PUT`** (perso) + **`POST /dashboard-layouts/company-default`** (`admin.configure_company`) + `DELETE /{id}` ; **`GET/POST /api/v1/saved-filters`** (`?viewKey=`) + `DELETE /{id}` ; **`GET/POST /api/v1/shortcuts`** + `DELETE /{id}` ; **`GET /api/v1/design-system/version`** |
| 58-59 | Tests / critères de sortie | `UxPersonalizationTests` (8) + `UxEndpointsTests` (2) + `personalizationApi.test.ts` (3). Le design system, les thèmes, la navigation, le POS, les tables, les formulaires, les erreurs, le feedback, le responsive, l'i18n : **validés** (Phases 13 / 19). WCAG audit outillé, régression screenshot, tests UX modérés, PWA, mode présentation : **backlog / exploitation** |
| 60 | Étape suivante — Phase 41 (Administration & maintenance) | hors périmètre |

## `V053` — contenu

- **`user_preference`** += `high_contrast` (bool), `text_scale` (`numeric(3,2)`, `CHECK 0.85–1.50`),
  `timezone` (text), `shortcuts` (jsonb — réservé).
- **`dashboard_layout`** — `company_id`, `user_id` NULL = défaut d'entreprise, `role` (ciblage §37),
  `widgets` jsonb `[{key,x,y,w,h,config}]`, `is_default`. Index uniques partiels : une seule
  disposition par défaut par `(entreprise, utilisateur)` et par `(entreprise, rôle)`. **RLS
  personnalisée** : ma disposition **ou** les défauts d'entreprise (`user_id NULL`) de mes
  entreprises ; le service filtre en outre explicitement (défense en profondeur §56).
- **`saved_filter`** — `user_id` NOT NULL, `view_key` (`« module.vue »`), `filter` jsonb, `is_default`,
  `shared`. `UNIQUE (user_id, view_key, name)`, index unique partiel du défaut. **RLS** : les miens
  **ou** les partagés de l'entreprise ; **écriture = propriétaire uniquement**.
- **`ui_shortcut`** — `user_id`, `chord`, `action`, `label`, `sort_order`. `UNIQUE (user_id, chord)`.
  **RLS** : propre à l'utilisateur.
- `enable`/`FORCE ROW LEVEL SECURITY` + `trg_audit_*` sur les 3 tables. **Aucune nouvelle
  permission** (libre-service) ; le défaut d'entreprise passe par `admin.configure_company`.
- **215 tables (+3), 185 RLS FORCE (+3)**, 53 migrations (V001–V053).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Preferences/PreferenceContracts.cs` (étendu) + `UxContracts.cs` |
| Application | `Nexora.Application/Preferences/IUxPersonalization.cs` — `IDashboardLayoutService`, `ISavedFilterService`, `IShortcutService`, `IDesignSystemService` |
| Infrastructure | `Preferences/PreferenceServices.cs` (`SqlUserPreferenceService` étendu) + `UxPersonalizationServices.cs` (`SqlDashboardLayoutService`, `SqlSavedFilterService`, `SqlShortcutService`, `DesignSystemService`) ; DI |
| API | `PreferencesController.cs` (alias `/user-preferences`, `/ui-preferences`, `/company-branding`) + `UxPersonalizationController.cs` + `DesignSystemController.cs` |

### Points de conception

- **RLS + filtre explicite** — les services `dashboard_layout` / `saved_filter` filtrent aussi la
  visibilité en SQL (`user_id = me OR shared`), la RLS servant de seconde barrière (§56) — le
  résultat reste correct même en portée système / superutilisateur.
- **Disposition effective** hiérarchique : perso → défaut de rôle → défaut d'entreprise
  (`GetEffectiveAsync`), les rôles étant lus via `IPermissionService.LoadProfileAsync`.
- **Accent & échelle de texte bornés** — luminance de l'accent ∈ 0,16–0,92 (Phase 13),
  `text_scale` ∈ 0,85–1,50 : la personnalisation ne peut jamais dégrader l'accessibilité (§5-6, §35).
- **`SqlQueryRaw` pour le bloc SELECT réutilisé** (`dashboard_layout`) — un `const string`
  interpolé deviendrait un paramètre.
- **`design-system/version` en dur dans le service** — la version (`1.1.0`) bump avec les releases
  du design system ; c'est le point de contrôle de compatibilité front / handoff (§50).

## Tests d'intégration — `UxPersonalizationTests` (8, `[Collection("finance")]`)

| Test | § | Vérifie |
|---|---|---|
| `Preference_accessibility_fields_round_trip_and_bounds` | 31-34 | `high_contrast` / `text_scale` / `timezone` persistés ; `text_scale = 3.0` → `preferences.text_scale` |
| `Dashboard_layout_personal_save_becomes_effective` | 13, 37 | disposition perso enregistrée → `GetEffectiveAsync` la renvoie (`UserId` posé) |
| `Dashboard_company_default_applies_without_a_personal_layout` | 37 | défaut d'entreprise (`user_id NULL`) → visible pour un autre utilisateur sans disposition perso |
| `Saved_filter_private_is_isolated_and_shared_is_visible` | 21-22 | filtre privé de D1 invisible pour D2 ; filtre partagé de D1 visible pour D2 (`Mine = false`) |
| `Saved_filter_only_one_default_per_view` | 22 | poser un 2ᵉ défaut retire le 1ᵉʳ |
| `Shortcut_crud_and_chord_conflict` | 11, 37-38 | création ; même `chord` → `shortcut.chord_taken` ; suppression |
| `Design_system_version_lists_tokens_and_components` | 50 | `wcagTarget` « 2.2 », thème `high-contrast`, composants (Button / CommandPalette / DataGrid), groupe `color` |
| `Dashboard_layout_rejects_too_many_widgets` | 13 | 50 widgets → `layout.widgets` |

## Tests API — `UxEndpointsTests` (2)

| Test | Vérifie |
|---|---|
| `Ux_routes_require_authentication` | `/design-system/version`, `/user-preferences`, `/saved-filters` sans jeton → 401 |
| `Preferences_alias_and_design_system_version_over_http` | `GET /user-preferences` 200 ; `PUT /ui-preferences {highContrast}` → `"highContrast":true` ; `GET /design-system/version` contient « WCAG 2.2 AA » + « CommandPalette » ; `GET /dashboard-layouts` 200 |

## Frontend

- `shared/theme.css` — **`--nx-text-scale`** (échelle de texte) + bloc **`[data-contrast="high"]`**
  (bords 2 px, encre renforcée, focus 3 px).
- `shared/PreferencesContext.tsx` — applique `data-contrast` + `--nx-text-scale` sur `<html>` avant
  peinture ; `UserPreferences` étendu (`highContrast`, `textScale`, `timezone`) ; bascule sur l'alias
  canonique `/user-preferences`.
- `shared/personalizationApi.ts` — `dashboard-layouts`, `saved-filters`, `shortcuts`,
  `design-system/version`.
- `settings/SettingsPage.tsx` — 2 onglets ajoutés : **Accessibilité** (contraste élevé, taille de
  texte S/M/L/XL/XXL, mouvement réduit, fuseau horaire) et **Raccourcis** (liste + ajout `<kbd>` +
  retrait). Pied de page « Design system NEXORA ONE v1.1.0 · WCAG 2.2 AA · 21 composants · 44 tokens ».
- Sélecteur de langue : ajout de « Kreyòl ayisyen » (§34).

Vitest `personalizationApi.test.ts` (3) → **66 tests frontend**.

## Smoke API (port 5080, `direction@abc-group.demo`)

- `GET /api/v1/design-system/version` → `v1.1.0 · WCAG 2.2 AA · 21 components · 44 tokens · themes [system, light, dark, high-contrast]`.
- `PUT /api/v1/user-preferences {highContrast:true, textScale:1.15, timezone:"America/New_York"}` → round-trip OK ; `{textScale:3.0}` → **422**.
- `PUT /api/v1/dashboard-layouts` (perso, is_default) → `GET` renvoie `effective = "Direction"`.
- `POST /api/v1/saved-filters {viewKey:"sales.list", isDefault:true}` → `mine = true`.
- `POST /api/v1/shortcuts {chord:"g v"}` OK ; même `chord` → **409**.

## Manques assumés (backlog)

- Audit WCAG outillé (axe-core en CI) + régression screenshot (Playwright / Chromatic) (§32, §52)
- Manifeste PWA + service worker (installation, cache contrôlé, push) (§47)
- Mode présentation plein écran du dashboard direction (§45)
- Traductions complètes en / ht-HT + tests de longues traductions + RTL réel (§34-35)
- Éditeur visuel de disposition de dashboard (drag & drop des widgets) — le stockage `dashboard_layout` est prêt (§13)
- Barre « filtres sauvegardés » branchée sur chaque écran de liste — le stockage `saved_filter` + `personalizationApi` sont prêts (§21-22)
- Centre d'aide intégré + guides par rôle / secteur (§55)
- Tests UX modérés (temps tâche, clics, satisfaction) (§53) — exploitation
