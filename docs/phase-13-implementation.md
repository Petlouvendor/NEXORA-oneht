# NEXORA — Phase 13 — État de l'implémentation

Référence : [`phase-13-ux-ui-design-system-premium.md`](phase-13-ux-ui-design-system-premium.md) +
[`design-system.md`](design-system.md).

## Principe directeur

Clarté avant décoration, rapidité avant complexité, hiérarchie visuelle forte, même logique
partout, interface adaptée au rôle, densité configurable, actions critiques explicites, données
financières lisibles sans surcharge, fonctionnement tactile / laptop / grand écran. La couleur
n'est jamais le seul porteur d'information (icône + texte + forme). Accessibilité WCAG visée.

## Ce qui est livré — `V032`

| Objet | Rôle |
|---|---|
| `user_preference` | préférences propres à l'utilisateur (§36) : `theme` (system/light/dark), `density` (comfortable/compact), `locale`, `reduced_motion`, `dashboard_mode` (summary/detailed), `favorites` jsonb, `table_columns` jsonb, `notification_prefs` jsonb. RLS **user-scoped** (`user_id`) — chacun gère les siennes |
| `company_branding` | personnalisation de l'entreprise (§35) : `display_name`, `accent_color` (`#RRGGBB`, CHECK format), `logo_url`, `receipt_format` (standard/compact/detailed), `document_footer`, `date_format`, `number_locale`, `default_locale`. RLS entreprise + audité |
| RLS | 142/153 tables protégées ; `user_preference` politique dédiée, `company_branding` via rattrapage `company_id` |

### Backend — `Nexora.{Application,Infrastructure}/Preferences`

```
IUserPreferenceService   GetAsync (défauts si absent), UpdateAsync (UPSERT partiel — seuls les
                         champs fournis changent ; theme/density/dashboard_mode validés)
ICompanyBrandingService  GetAsync (défauts si absent), UpdateAsync — accent validé :
                         format #RRGGBB + LUMINANCE RELATIVE bornée [0.16 ; 0.92] pour ne jamais
                         dégrader le contraste (§35) ; logo restreint à https:// ou data:image/
```

`GET /api/v1/me` renvoie désormais aussi `preferences` et `branding` → le SPA obtient tout son
contexte UX en un appel.

| Route | Accès |
|---|---|
| `GET/PUT /me/preferences` | authentifié (libre-service, isolé par RLS) |
| `GET /companies/branding` | tout membre |
| `PUT /companies/branding` | `admin.configure_company` |

### Frontend — le design system

**`shared/theme.css`** — refonte complète : palette **sombre par défaut** + **mode clair**
(`:root[data-theme="light"]`) + **mode système** (`@media prefers-color-scheme` sur
`[data-theme="system"]`) ; échelle d'espacement / typo / élévation en tokens ; **densité**
(`[data-density="compact"]`) ; **mouvement réduit** (`[data-motion="reduced"]` + respect de
`prefers-reduced-motion`) ; `:focus-visible` clavier uniquement ; lien d'évitement ; styles
d'impression pour les documents.

**`shared/ui.tsx`** — bibliothèque de composants partagée (§29-30) : `Button` (primary / ghost /
subtle / danger, loading, block), `IconButton`, `Field` + `TextInput` + `Select` (obligatoire,
erreur, invalide, lecture seule), `Card`, `Badge`, `StatePill` (couleur d'état dérivée + point),
`Money` (signe + tabular-nums), `KpiCard`, `Tabs`, `Modal`, `Drawer`, `ConfirmDialog` (résumé
d'impact + motif consigné, §20), `EmptyState`, `Skeleton`, `Spinner`, `Tooltip`, `HelpTip`
(§39), `DataTable` (colonnes numériques alignées, totaux, état vide).

**`shared/Toast.tsx`** — file de notifications éphémères (`aria-live`), tons succès / erreur.

**`shared/icons.tsx`** — jeu d'icônes propriétaire (trait unique 1.6, 24 icônes cohérentes,
monogramme « N ») (§31).

**`shared/PreferencesContext.tsx`** — applique `data-theme` / `data-density` / `data-motion` au
`<html>` **avant peinture** (`useLayoutEffect`) ; dérive `--nx-brand` + encre de l'accent
d'entreprise ; charge les préférences serveur à l'authentification, recharge la personnalisation
au changement d'entreprise ; sauvegarde optimiste (localStorage immédiat + PUT différé 400 ms).
Bootstrap dans `main.tsx` : le thème mis en cache est appliqué avant le premier rendu (pas de
flash — §41).

**`shared/format.ts` / `shared/i18n.ts`** — formatage monétaire / date / nombre sensible à la
langue (fr-FR par défaut) ; dictionnaire FR + `t()` prêt pour d'autres langues (§40).

**Coquille applicative (`app/AppLayout.tsx`)** — sidebar premium : monogramme, sélecteur
d'entreprise, section **Favoris**, navigation **groupée** (Opérations / Finance / Pilotage /
Configuration) **filtrée par permission** (§9) ; barre de contexte permanente (monogramme →
groupe → entreprise, nom = personnalisation) ; **badge En ligne / Hors ligne** (§25) ; bouton
recherche, cloche de notifications, **bascule de thème**, menu compte avec **déconnexion visible**
et indicateur de session (§42). Responsive : la sidebar devient un tiroir < 900 px.

**`app/CommandPalette.tsx`** — recherche / navigation globale au clavier (**Ctrl/⌘ + K**),
respecte les permissions (§23), navigation ↑↓ / ⏎.

**Écrans retravaillés** :
- `dashboard/DashboardPage.tsx` — `KpiCard`, bascule **Vue résumé / détaillée** (persistée dans
  les préférences), actions rapides filtrées par permission, checklist d'onboarding, tendance en
  graphe simple avec `aria-label`, alertes avec icône + couleur.
- `dashboard/OnboardingChecklist.tsx` — assistant de démarrage en 5 étapes, repliable, « à
  terminer plus tard » (§38).
- `pos/PosPage.tsx` — layout §13 (recherche/scan → panier toujours visible → **Montant dû**
  géant → paiement) ; rendu monnaie évident, **reste à crédit** clairement identifié ;
  `ConfirmDialog` avec résumé d'impact **avant la transaction irréversible** (§14, §20) ;
  indicateur hors ligne ; focus scanner permanent.
- `auth/LoginPage.tsx` — carte premium, monogramme, dégradé d'accent discret.
- `settings/SettingsPage.tsx` (remplace le placeholder) — onglet **Apparence & langue** (thème,
  densité, langue, mode dashboard, mouvement réduit) + onglet **Personnalisation entreprise**
  (nom, accent avec aperçu et sélecteur natif, logo, format de reçu, pied de page) réservé à
  `admin.configure_company`.
- `shared/ModulePlaceholder.tsx` — `EmptyState` cohérent pour les modules à écran différé.

### Démo

`demo_abc_group.sql` : `company_branding` pour les 3 entreprises (Pharmacie `#00F5D4` reçu
standard, Restaurant `#FF9F45` reçu détaillé, Supermarché `#5B8DEF` reçu compact), pieds de page.
Nettoyage feuilles → racine étendu (`company_branding` ; `user_preference` suit la cascade
`app_user`). Rejouable.

## Vérifications exécutées

**Tests d'intégration `Nexora.IntegrationTests/UxPreferencesTests` — 6/6** :

| Scénario (§35-36) | Vérifié |
|---|---|
| Préférences par défaut + aller-retour + fusion partielle | défauts si absent ; `dark`/`compact` persistés ; une MAJ « langue seule » conserve thème/densité/mouvement |
| Isolation par utilisateur | d1 en `dark`, d2 en `light` → chacun voit la sienne (RLS `user_id`) |
| Valeurs hors référentiel refusées | `theme=neon`, `density=ultra`, `dashboard_mode=3d` → rejet |
| Personnalisation d'entreprise bornée au périmètre | c1 et c2 ont des accents distincts ; modifier c2 ne touche pas c1 |
| Accent qui casserait l'accessibilité refusé | `#000000` (trop sombre) et `#FFFFFF` (trop clair) et `pas-une-couleur` → rejet ; `#3AA0FF` → accepté |
| Colonnes de tableau + préférences de notification (jsonb) | round-trip fidèle |

**Smoke API (démo)** : `/me` renvoie `preferences` + `branding` ; `PUT /me/preferences`
(`dark`/`compact`/favoris) persiste et se relit dans `/me` ; `PUT /companies/branding` avec
`#000000` → **422** ; avec `#12E8C6` + reçu `detailed` → **200** et relu dans `/me`.

**Frontend** : `tsc` strict + `vite build` OK (74 modules). Sidebar, thème clair/sombre/système,
densité, palette Ctrl+K, POS premium, settings — construits.

**Total backend : 104 tests verts** (16 unitaires + 2 API + 86 intégration), build Debug **et**
Release 0 warning. DB : 32 migrations (V001–V032), **153 tables**, 142 avec RLS. Rebuild + smoke
+ démo (rejouée 2×) OK.

## Écarts et décisions

- **Écrans métier non encore construits** (Finance §15, Comptabilité §16-17, tables de données
  configurables §18, formulaires génériques §19, centre de notifications §21-22, documents /
  impression §34) : le **design system, les composants et les états sont livrés et prêts** ;
  chaque écran sera habillé avec ces briques dans sa phase / itération dédiée. Le placeholder
  utilise désormais l'`EmptyState` du système.
- **Recherche globale** (§23) : palette clavier livrée sur la **navigation** ; la recherche
  d'entités (produits, ventes, écritures…) réutilisera la même palette quand les endpoints de
  recherche transverses existeront — elle respecte déjà strictement les permissions.
- **i18n** (§40) : architecture + formatteurs + dictionnaire FR livrés ; les autres langues
  s'ajoutent en déposant un dictionnaire. Les fuseaux horaires s'appuient sur `company.timezone`
  (déjà en base).
- **Logo & documents** : `company_branding.logo_url` est stocké et validé ; le rendu dans les
  reçus / factures PDF est du ressort du module Documents.
- **Widgets réorganisables du dashboard** (§11) : la bascule résumé / détaillé et la
  personnalisation par rôle sont livrées ; le glisser-déposer des widgets est reporté
  (`user_preference.table_columns` / `notification_prefs` posent déjà le modèle de stockage).
- **Tests UX** (§43) : les scénarios sont **définis** ; l'automatisation end-to-end navigateur
  (clavier, tactile, mode sombre, gros volumes) n'est pas dans cette phase — la validation ici
  est faite via les tests d'intégration back + le build strict front.
- **Onboarding** (§38) : checklist livrée (5 étapes, repliable) ; l'assistant pas-à-pas complet
  de création d'entreprise relève de la Phase 14 (Administration & Paramétrage).

## Couverture des critères de sortie (§44)

| Critère | État |
|---|---|
| Identité visuelle / palette / typographie / grille | ✅ tokens sombre + clair, Inter + chiffres tabulaires, échelle d'espacement |
| Navigation | ✅ sidebar groupée, filtrée par permission, favoris, contexte permanent, Ctrl+K |
| POS | ✅ layout §13-14, montant dû géant, rendu monnaie, crédit identifié, confirmation avant vente |
| Dashboards | ✅ Command Center KpiCard + résumé/détaillé + onboarding + alertes |
| Finance / Comptabilité / Multi-entreprises | ⚙️ briques du design system prêtes ; écrans habillés progressivement |
| Design system / états des composants | ✅ bibliothèque `shared/ui` + 11 états couverts |
| Responsive | ✅ desktop prioritaire, sidebar-tiroir < 900 px, tableaux à défilement propre |
| Accessibilité | ✅ focus-visible, lien d'évitement, `aria-live`/`role`, cibles 40 px, couleur + icône, `prefers-reduced-motion` |
| Onboarding | ✅ checklist §38 |
| Internationalisation | ✅ architecture + formatteurs + dictionnaire FR |
| Tests UX | ✅ scénarios définis ; validation back automatisée |

## Suite — Phase 14

Administration, Paramétrage & Automatisation : centre d'administration permettant à chaque
entreprise et au groupe d'adapter NEXORA sans développeur.
