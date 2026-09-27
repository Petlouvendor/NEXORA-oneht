# NEXORA — Phase 9 — État de l'implémentation

Référence : [`phase-9-multi-entreprises-groupe-consolidation.md`](phase-9-multi-entreprises-groupe-consolidation.md).

## Principe directeur

Chaque entreprise reste **autonome** : comptabilité, finance, stock, utilisateurs propres,
isolés par `company_id` / RLS. La **vue groupe** est autorisée par permission. La
**consolidation** est un **processus distinct** : elle agrège des copies *converties* et
*mappées* des balances individuelles, applique les éliminations et contrôle la cohérence —
sans jamais écrire dans les livres sources (§2, §13, §16).

## Ce qui est livré

### Base de données — `V028`

| Objet | Rôle |
|---|---|
| `group_account` | plan comptable groupe ; `is_eliminable` marque les comptes intragroupe (§12, §16) |
| `account_group_mapping` | mapping compte local → compte groupe, **versionné et daté** (nouvelle version ⇒ ancienne `superseded`, `effective_to` posé) ; index unique partiel sur `(account_id) WHERE status='active'` (§12) |
| `group_policy` | devise de consolidation, méthode de change (`closing_rate`/`average_rate`/`historical`), tolérance intercompany (§26) |
| `intercompany_match` | rapprochement : `matched` / `asymmetric` / `missing_counterpart`, écart de montant, correspondance devise, écart de date (§15) |
| `consolidation_run` | processus : `draft → preparing → reconciling → ready → validated → published → reopened`, `coherence_ok`, totaux, `diagnostics` jsonb (§17-18) |
| `consolidation_run_company` | inclusion + `fx_rate` + `period_closed` + `mapping_complete` + contribution par entreprise (§13, §17) |
| `consolidation_run_line` | balance consolidée par compte groupe : agrégé / élimination / consolidé (§19) |
| `group_task` | tâches de contrôle groupe (§25) |
| `company.archived_at` / `archived_by` | archivage d'entreprise (§29) |
| `intercompany_transaction` | `+ UNIQUE (id, group_id)` (FK de `intercompany_match`) |
| permissions | `group.{view,manage,mappings,intercompany,tasks,companies}`, `consolidation.{run,validate,publish}` + rattachement aux rôles (comptable en chef = tout consolidation, comptable simple = `group.view`) |
| RLS | `enable_group_rls` sur les 7 tables portées par le groupe (isolation par `X-Group-Scope`) + rattrapage `company_id` (§22) — 128/138 tables protégées |

Réutilise `V014` : `intercompany_transaction`, `consolidation_profile`, `consolidation_scope`,
`consolidation_adjustment` ; `V003` : `business_group`, `company.group_id`.

### Backend — `Nexora.Application/Group` + `Nexora.Infrastructure/Group`

```
IGroupLedgerService        plan comptable groupe + mappings versionnés + couverture + politique (§12, §26)
IIntercompanyService       enregistrer / confirmer / rapprocher (auto-appariement sens inversé) (§14-15)
IConsolidationService      créer un run → exécuter → valider → publier → rouvrir ; états consolidés (§17-20)
IGroupControlCenter        centre de contrôle du comptable en chef : par entreprise, clôtures,
                           comptes non mappés, intercompany non rapproché, créances/dettes (§23-24)
IGroupTaskService          tâches de contrôle groupe (§25)
ICompanyProvisioningService  ajouter une entreprise (plan comptable + période + journaux) ; archiver (§28-29)
```

**Garde de périmètre.** Chaque méthode appelle `GroupGuard.Require(tenant, groupId)` :
`403 group.scope` si le groupe n'est pas dans `tenant.GroupIds` (rempli uniquement quand
l'en-tête `X-Group-Scope: true` est présent). Les lectures transverses passent par
`ITenantContext.BeginSystemScope()` (§21-22).

**Algorithme de consolidation (`ExecuteAsync`).** Pour chaque entreprise incluse :
taux de change fonctionnelle → devise de consolidation (`exchange_rate`, date d'arrêté) ;
vérification (non bloquante) de la clôture de période et de la complétude du mapping ;
agrégation de `journal_entry_line.base_debit/credit` (écritures `posted` de la période) par
compte groupe via le mapping actif, **converti** au taux. Éliminations : pour les
transactions intercompany `matched` de la période, on retire le montant du compte groupe
**actif éliminable** (débit) et du compte groupe **passif éliminable** (crédit) — `Σ élim.
débit = Σ élim. crédit`, l'équilibre est préservé. Contrôle de cohérence : `Σ consolidé
débit = Σ consolidé crédit` (§20). `status = reconciling` s'il reste de l'intercompany non
rapproché, sinon `ready`.

### API — `v1`

| Route | Permission |
|---|---|
| `GET /groups/{id}/accounts` · `POST .../accounts` · `PUT .../accounts/mappings` · `GET .../accounts/mapping-coverage` | `group.mappings` |
| `GET /groups/{id}/policy` (view) · `PUT .../policy` (`group.manage`) | `group.view` / `group.manage` |
| `GET/POST /groups/{id}/intercompany` · `POST .../{tx}/confirm` · `POST .../reconcile` · `GET .../unreconciled` | `group.intercompany` |
| `GET /groups/{id}/control-center` | `group.view` |
| `GET/POST /groups/{id}/tasks` · `PUT .../tasks/{taskId}` | `group.tasks` |
| `POST /groups/{id}/companies` · `POST .../companies/{cid}/archive` | `group.companies` |
| `GET /groups/{id}/consolidation/runs` · `GET .../{runId}` · `GET .../{runId}/statements` | `group.view` |
| `POST .../consolidation/runs` · `.../execute` | `consolidation.run` |
| `POST .../{runId}/validate` · `.../reopen` | `consolidation.validate` |
| `POST .../{runId}/publish` | `consolidation.publish` |

Toutes exigent `X-Group-Scope: true` ; le serveur re-vérifie l'autorisation à chaque appel
(§21-22). `403` sans l'en-tête, `403` pour un rôle sans droit groupe.

### Frontend

`frontend/nexora-web/src/group/GroupPage.tsx` (route `/group`, nav « Groupe & Consolidation ») :
tuiles de synthèse, centre de contrôle du comptable en chef (par entreprise : période,
comptes non mappés, intercompany non rapproché, créances/dettes), liste des consolidations
avec diagnostics et actions **Exécuter / Valider / Publier** selon le statut et les
permissions. `groupApi.ts` force l'en-tête `X-Group-Scope`. `npm run typecheck` + `build` OK.

### Démo

`demo_abc_group.sql` : comptabilité minimale du **Restaurant ABC** (plan comptable, comptes,
journaux, période) ; **politique groupe** (consolidation en HTG) ; **plan comptable groupe**
(9 comptes, `GA-AR` / `GA-AP` éliminables) ; **mapping** des comptes locaux Pharmacie + Restaurant
vers le plan groupe (31 lignes actives). Rejouable (nettoyage étendu aux tables groupe).

## Vérifications exécutées

**Tests d'intégration `Nexora.IntegrationTests/GroupConsolidationTests` — 8/8** :

| Scénario (§33) | Vérifié |
|---|---|
| Mapping groupe versionné | re-mapping d'un compte → ancienne ligne `superseded`, nouvelle `active` |
| Vente intercompany + achat correspondant + rapprochement | auto-appariement sens inversé → `matched` ; la contrepartie sort des « non rapprochés » ; 5000 vs 4800 → `asymmetric` (écart 200) |
| Agréger + convertir + bilan consolidé équilibré | 2 entreprises, écritures postées → balance consolidée : `Σ débit = Σ crédit`, CA 20 000, résultat 10 000, bilan équilibré, contributions par entreprise |
| Élimination intercompany | vente/achat intragroupe 6 000 rapprochés → `GA-AR` élimination débit 6 000, `GA-AP` élimination crédit 6 000, consolidé = 0, balance toujours équilibrée, `elimination_total = 6 000` |
| Cycle de vie | valider avant exécution → refus ; publier avant validation → refus ; `ready → validated → published` ; réouverture sans motif → refus, avec motif → `reopened` |
| Centre de contrôle + tâches | synthèse des 3 entreprises ; tâche créée / listée / passée à `done` |
| Ajouter une entreprise sans nouvelle installation, puis archiver | plan comptable + période + 5 journaux + activité principale câblés ; archivage → `status='archived'`, `archived_at` posé, modules désactivés, **5 journaux conservés** ; motif obligatoire |
| Accès hors périmètre groupe | `tenant.GroupIds` vide → `DomainException` / `ErrorKind.Forbidden` sur les services groupe |

Smoke API (démo, utilisateur `compta@abc-group.demo` = comptable en chef) : `group/accounts` (9),
`mapping-coverage` (Pharmacie 20 mappés / 0 non mappé, Restaurant 11 / 0), `policy`,
`control-center` (3 entreprises), `POST intercompany`, `POST consolidation/runs` → `draft`,
`.../execute` → `reconciling`, `coherence_ok`, `.../statements` → balance équilibrée. Sans
`X-Group-Scope` → **403** ; caissier → **403**.

**Total backend : 72 tests verts** (16 unitaires + 2 API + 54 intégration), build Release 0 warning.
DB : 28 migrations (V001–V028), **138 tables**, 128 avec RLS. Rebuild + smoke + démo (rejouée) OK.

## Écarts et décisions

- **Éliminations** : modèle simplifié sur les comptes groupe éliminables (une paire actif +
  passif). Les éliminations produits/charges internes, dividendes et l'imputation par
  transaction (§16) sont définies mais reportées ; `consolidation_adjustment` (`V014`) reste
  disponible pour les ajustements manuels (§17 étape 9).
- **Conversion de devises** : `closing_rate` (taux d'arrêté depuis `exchange_rate`).
  `average_rate` / `historical` et les différences de conversion (§13) sont stockés dans la
  politique mais un seul mode est appliqué.
- **États consolidés** : balance consolidée + agrégats bilan / résultat. Le flux de
  trésorerie consolidé et le grand livre de consolidation (§19) sont reportés.
- **Provisioning d'entreprise** : plan comptable *vide* + période + journaux. Taxes,
  établissements, utilisateurs et mappings restent à configurer ensuite (§28).
- **Réauthentification pour actions critiques** (§22) : les permissions dédiées
  (`consolidation.validate` / `.publish`) et l'audit couvrent le besoin ; la ré-auth MFA
  explicite est reportée.
- **Vue groupe (§9-10)** : le Command Center groupe de la Phase 7 (`GET /dashboard/groups/{id}`)
  reste la vue KPI ; il exclut désormais les entreprises `archived` (§29).

## Couverture des critères de sortie (§34)

| Critère | État |
|---|---|
| Groupe / entreprise indépendante / multi-sites | ✅ `business_group` nullable, `company` autonome, établissements (Phase 3) |
| Utilisateurs multi-entreprises / sélecteur | ✅ `access_scope` + rôles par périmètre (Phase 3) ; sélecteur front (Phase 3) |
| Vue groupe | ✅ Command Center groupe (Phase 7) + centre de contrôle (Phase 9) |
| Comptabilité multi-entreprises | ✅ plan / journaux / périodes propres (Phase 4) |
| Mapping groupe | ✅ `account_group_mapping` versionné + couverture |
| Intercompany / rapprochement / éliminations | ✅ `IIntercompanyService` + éliminations de consolidation |
| Consolidation / devise de consolidation | ✅ `IConsolidationService` (agrège / convertit / élimine / contrôle) |
| Sécurité et isolation | ✅ RLS groupe + `X-Group-Scope` + garde serveur + audit (triggers) |
| Cabinet / comptable en chef | ✅ `IGroupControlCenter` + tâches |
| Scénarios testables | ✅ 8 scénarios §33 automatisés |

## Suite — Phase 10

Local, Offline, Hybride & Cloud : même NEXORA sur un poste isolé, plusieurs sites connectés,
en cloud, ou avec des caisses fonctionnant temporairement hors connexion.
