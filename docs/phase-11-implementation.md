# NEXORA — Phase 11 — État de l'implémentation

Référence : [`phase-11-securite-audit-conformite-antifraude.md`](phase-11-securite-audit-conformite-antifraude.md).

## Principe directeur

La sécurité est une fonction centrale : deny-by-default, moindre privilège, séparation des
responsabilités, traçabilité des opérations sensibles, aucune suppression silencieuse d'un
historique financier, validation côté serveur, sécurité identique en local / cloud / hybride.
Les mécanismes anti-fraude **alertent** sans remplacer le jugement humain (§1-2).

## Acquis réutilisé

Le socle sécurité était déjà en place : IAM (`access_scope`, `user_role`, `permission`,
deny-by-default `RequirePermission`, RLS multi-tenant), `audit_log` + trigger `audit_row()`
sur 15 tables sensibles (V016/V019), immuabilité des écritures et périodes clôturées (V019),
`approval_policy` / `approval_request` / `approval_action` (V016), hachage bcrypt + JWT,
`export_log`, révocation d'appareils de synchronisation (Phase 10).

## Ce qui est livré — `V030`

| Objet | Rôle |
|---|---|
| `fraud_rule` | catalogue de **12 règles** anti-fraude (référence, hors RLS) : annulations fréquentes, annulation après paiement, remboursements anormaux, prix forcés, écarts de caisse, ventes à crédit inhabituelles, ajustements de stock fréquents, pertes répétées, extournes répétées, modification fournisseur… (§14-16) |
| `fraud_rule_setting` | activation + seuils + fenêtre par entreprise |
| `fraud_signal` | signaux **notés 0-100**, sévérité `low/medium/high/critical`, statut `open→reviewing→confirmed/false_positive/dismissed` ; dédup par jour de scan ; aucune suppression sans trace (§17-18) |
| `step_up_challenge` | ré-authentification renforcée : jeton à usage unique, TTL court, par motif (§7, §36) |
| `user_session` + `app_user.security_epoch` | registre des sessions + **déconnexion globale** : tout jeton émis avant l'`epoch` est refusé (§6, §30) |
| `security_policy` | politique par entreprise : rôles à MFA obligatoire, TTL session / step-up, filigrane d'export, rétention d'audit (§7, §21, §31) |
| trigger `forbid_audit_mutation` | le journal d'audit est **immuable** : `UPDATE` / `DELETE` sur `audit_log` lèvent une exception (§12, §39) |
| audit étendu | `fraud_signal`, `approval_request`, `approval_action`, `security_policy`, `user_role`, `access_scope`, `supplier`, `customer`, `sync_device` sont désormais audités (§11) |
| permissions | `security.{audit_view,fraud_review,approvals,sessions,configure}` + rattachement aux rôles |
| RLS | politiques utilisateur sur `user_session` / `step_up_challenge` + rattrapage `company_id` — 135/146 tables protégées |

### Backend — `Nexora.Application/Security` + `Nexora.Infrastructure/Security`

```
IAuditQueryService       QueryAsync (paginé, borné à tenant.CompanyIds), ExportAsync (CSV journalisé, sensibilité 'restricted')
IFraudDetectionService   ScanAsync (10 règles implémentées sur l'activité récente → fraud_signal notés),
                         ListAsync, ReviewAsync (note obligatoire pour confirmer / écarter)
IApprovalWorkflowService SubmitAsync (seuil de politique → not_required / pending),
                         DecideAsync (demandeur ≠ approbateur ; approbateurs distincts en double niveau ; §5),
                         ListAsync / GetAsync
IStepUpAuthService       ChallengeAsync (vérifie le mot de passe → jeton), VerifyAsync (usage unique, non expiré)
ISessionSecurityService  RecordAsync (à l'ouverture), ListAsync, RevokeAsync, RevokeAllAsync (bump security_epoch)
ISecurityPolicyService   GetAsync / SetAsync
```

**Notation du risque** : `score = base(sévérité) + min(20, dépassement × 20)`, borné 0-100 ;
la sévérité affichée est dérivée du score.

**Déconnexion globale** : `TenantResolutionMiddleware` compare la claim `iat` du jeton à
`app_user.security_epoch` (une lecture PK par requête) ; jeton antérieur ou compte non
`active` → `401 session_revoked`. Vérifié bout-en-bout au smoke.

### API — `v1`

| Route | Permission |
|---|---|
| `GET /security/audit` · `POST /security/audit/export` | `security.audit_view` |
| `POST /security/fraud/scan` · `GET /security/fraud/signals` · `POST .../signals/{id}/review` | `security.fraud_review` |
| `POST /security/approvals` · `GET /security/approvals` · `GET .../{id}` · `POST .../{id}/decide` | `security.approvals` |
| `POST /security/step-up` · `POST /security/step-up/verify` | authentifié |
| `GET /security/sessions` · `POST .../sessions/{id}/revoke` · `POST .../sessions/revoke-all` | authentifié (propres sessions) |
| `GET /security/policy` · `PUT /security/policy` | `security.configure` |

`AuthController.Login` enregistre désormais une `user_session` (meilleur effort, ne bloque pas l'authentification).

### Frontend

`frontend/nexora-web/src/security/SecurityPage.tsx` (route `/security`, nav « Sécurité ») :
onglets **Anti-fraude** (balayage + signaux notés + revue), **Approbations** (demandes en
attente + approuver/refuser), **Journal d'audit** (recherche, changements avant/après),
**Sessions** (révocation individuelle + déconnexion globale). Onglets filtrés par permission.
`npm run typecheck` + `build` OK.

### Démo

`demo_abc_group.sql` : `security_policy` par défaut + 12 règles anti-fraude actives pour les
3 entreprises ; seuils d'approbation de la Pharmacie ABC (dépense ≥ 25 000, **remboursement
≥ 5 000 en double approbation**, remise ≥ 3 000, ajustement stock ≥ 10 000, clôture de
période). Rejouable.

## Vérifications exécutées

**Tests d'intégration `Nexora.IntegrationTests/SecurityAuditTests` — 7/7** :

| Scénario (§38-40) | Vérifié |
|---|---|
| Chaque action sensible crée un événement d'audit avant/après | révision de prix → événements `INSERT` + `UPDATE` (avec `valid_to` dans les changements), bon utilisateur, bonne entreprise |
| Le journal d'audit est immuable | `UPDATE` et `DELETE` sur `audit_log` → exception |
| Recherche d'audit bornée au périmètre | utilisateur limité à c2 → aucun événement de c1 |
| Détection d'une activité anormale | 3 ventes à crédit du même utilisateur → `fraud_signal` `pos_unusual_credit_sales` noté ; **confirmer exige une note** (§17) |
| Double approbation | seuil `refund ≥ 1 000` niveaux 2 : demande 5 000 → requise ; **demandeur ne s'approuve pas** ; **même approbateur ≠ deux niveaux** ; deux approbateurs distincts → `approved` ; sous le seuil → non requise |
| Ré-authentification renforcée | mauvais mot de passe → échec ; jeton **à usage unique** (2ᵉ vérif → faux) ; motif différent → faux |
| Sessions + déconnexion globale | listage, révocation individuelle (motif obligatoire), `revoke-all` révoque tout **et avance `security_epoch`** |

Smoke API (démo) : `audit` (27 événements paginés), `fraud/scan` (10 règles), `fraud/signals`,
`approvals` (refund 8 000 → 2 niveaux), auto-approbation → **403**, `step-up` mauvais mot de
passe → **401**, jeton correct → vérif `true` puis `false`, `sessions`, caissier sur `audit`
→ **403**, **déconnexion globale → ancien jeton refusé (401)**.

**Total backend : 87 tests verts** (16 unitaires + 2 API + 69 intégration), build Release 0 warning.
DB : 30 migrations (V001–V030), **146 tables**, 135 avec RLS. Rebuild + smoke + démo (rejouée) OK.

## Écarts et décisions

- **Moteur anti-fraude** : 10 des 12 règles sont implémentées (`pos_unusual_discounts` et
  `finance_expense_under_threshold` sont définies, non branchées) ; le scan est **à la
  demande** (`POST /security/fraud/scan`) — la planification est reportée à la phase
  intégrations. Historique comportemental (§17) : le seuil est global, pas encore appris par
  utilisateur.
- **Approbations** : le service crée et fait avancer la demande ; le **rattachement à
  l'exécution** de l'opération (bloquer un remboursement tant que la demande n'est pas
  approuvée) reste à câbler dans chaque parcours (POS, dépenses, extournes).
- **Sessions** : la révocation individuelle est un marqueur du registre ; l'invalidation
  effective des jetons se fait globalement via `security_epoch` (les jetons vivent ≤ 60 min).
- **MFA** (§6-7) : `security_policy.mfa_required_roles` est en place et `app_user.mfa_enabled`
  existe (Phase 3) ; le second facteur TOTP lui-même n'est pas encore implémenté — le
  step-up par mot de passe couvre la ré-authentification.
- **Chiffrement au repos, protection des sauvegardes, gestion d'incident, rate limiting,
  headers de sécurité, protection des uploads** (§21, §24-30) : en partie déjà en place
  (Phase 3 : `SecurityHeadersMiddleware`, `UseRateLimiter`, TLS en production) ; le reste
  relève de l'exploitation et de l'infrastructure, **défini** dans le document de phase.
- **Conformité / fiscalité** (§31-32) : NEXORA est conçu pour la conformité (audit, périodes,
  numérotation, rétention paramétrable) mais ne prétend pas la garantir — validation par un
  professionnel compétent (§31).

## Couverture des critères de sortie (§41)

| Critère | État |
|---|---|
| IAM / permissions / scopes / séparation des responsabilités | ✅ Phase 3 + approbations (demandeur ≠ approbateur) |
| MFA | ⚙️ politique + `mfa_enabled` en place ; step-up par mot de passe livré ; TOTP reporté |
| Audit complet | ✅ 24 tables auditées, journal immuable, recherche bornée + export journalisé |
| Anti-fraude | ✅ moteur de règles + signaux notés + revue tracée |
| Approbations | ✅ simple / double, expiration via politique, historique |
| Protection des données / exports | ✅ export d'audit `restricted` + journalisé ; masquage des changements techniques |
| API / DB sécurisées | ✅ TLS, rate limiting, headers, deny-by-default, RLS `NOBYPASSRLS`, migrations suivies |
| Offline sécurisé | ✅ Phase 10 (appareils révocables, politique offline, opérations uniques) |
| Sauvegardes / incidents / gouvernance | ⚙️ procédures d'exploitation définies |
| Tests de sécurité et anti-fraude | ✅ 7 scénarios §38-40 automatisés + isolation multi-tenant testée depuis la Phase 3 |

## Suite — Phase 12

Intégrations, Paiements & Écosystème : banques, Mobile Money, terminaux de paiement,
imprimantes, scanners, balances, e-commerce, messagerie, API externes.
