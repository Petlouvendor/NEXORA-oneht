# 07 — Audit de sécurité et d'isolation des entreprises

> Audit applicatif par lecture du code, requêtes de catalogue PostgreSQL et requêtes HTTP contre l'API de HEAD
> (port 5199, base clonée `nexora_audit_api`). **Aucun test intrusif, aucun environnement de production.** Aucun outil
> de scan dynamique n'a été lancé. Les scénarios d'écriture ont été arrêtés à la demande du contrôle de permissions de
> l'outil : les défauts qui exigent une écriture pour être démontrés sont classés « Lecture ».

## 1. Synthèse

| Domaine | Constat | Verdict | Anomalies |
|---|---|---|---|
| Authentification | BCrypt, verrouillage après 5 échecs (15 min), jetons de 15 min, cookie de rafraîchissement HttpOnly avec rotation et détection de rejeu, MFA TOTP à secret chiffré AES-256-GCM | Solide | A-011 (FAIBLE) |
| Confirmation forte | 41 actions ; jeton lié à la session (`sid`) | **Vérifié** (refus en session B, accepté en session A) | — |
| Autorisation | `FallbackPolicy` authentifiée (deny-by-default) ; `[RequirePermission]` sur 954 des 1 029 points d'accès ; 57 en libre-service ; 18 anonymes justifiés | Solide sur la forme | A-010, A-014 |
| Isolation par requête | `X-Company-Id` doit figurer dans le jeton | **Vérifié** : étranger → 403, invalide → 400 | — |
| Isolation en base | RLS activée et forcée sur 250/291 tables ; rôle `nexora_app` sans BYPASSRLS ; tables propriété d'un autre rôle | **Vérifié** (catalogue) | A-009, A-029, A-030 |
| Isolation, console d'exploitation | `/ops/*` agit à l'échelle de la plateforme pour des rôles d'entreprise | **Défaut** (lecture) | **A-031 (CRITIQUE en SaaS)** |
| Injection SQL | SQL brut paramétré (`FormattableString`, `SqlQueryRaw` à paramètres positionnels) ; identifiants dynamiques limités à des listes fermées (`SqlDataCorrectionService`) ; script RLS construit à partir de GUID formatés | Aucune injection trouvée | — |
| XSS | React (échappement par défaut) ; CSP stricte servie par Caddy pour l'interface (`script-src 'self'`) ; en-têtes de sécurité de l'API | Aucune faille relevée : aucun `dangerouslySetInnerHTML`, `eval` ni `new Function` ; un seul `innerHTML`, avec une chaîne constante (`shared/printApi.ts:46`) | — |
| CSRF | Jeton d'accès en en-tête `Authorization` (pas de cookie d'authentification) ; cookie de rafraîchissement HttpOnly, `same-origin` | Risque faible | — |
| SSRF | Webhooks sortants : refus des adresses internes à l'enregistrement et après résolution DNS, sans redirection | **Vérifié** par tests unitaires (WebhookTargetPolicyTests) | — |
| Secrets | Fichiers de secrets en production ; démarrage refusé sans clé (StartupSecretsGuard) ; clé de repli de développement acceptée seulement en Development | Solide | — |
| Exposition | `/metrics` anonyme, blocage Caddy par chemin exact | Défaut mineur | A-024 |
| Journal d'audit | `audit_log` immuable par déclencheur | Vérifié (catalogue) | A-030 |
| Limitation de débit | auth 10/min, refresh 60/min, public 30/min par IP | Présente (non testée en charge) | — |
| Dépendances | Scan présent en CI mais non bloquant | Défaut mineur | A-028 |

## 2. Isolation multi-entreprise : détail

1. **Requête** : `TenantResolutionMiddleware` refuse un `X-Company-Id` absent du jeton (vérifié, 403).
2. **Base** : chaque connexion reçoit `nexora.company_ids`, `group_ids`, `user_id`, `establishment_ids` ; les politiques
   `rls_allows_company()` filtrent lectures et écritures (227 politiques).
3. **Faiblesses** :
   - les variables ne sont posées qu'à l'ouverture de connexion : une portée système ouverte dans une transaction est
     sans effet, une transaction ouverte dans une portée système la prolonge (A-009, effet constaté par lecture :
     réinitialisation MFA qui laisse les codes de secours) ;
   - `nexora.bypass_rls` est positionnable par le rôle applicatif lui-même ;
   - tables d'identité (`app_user`, `user_role`, `access_scope`, `role_permission`) sans RLS ;
   - **38 fichiers** ouvrent une portée système. Revue de chacun : 36 bornent leurs requêtes à l'entreprise, au groupe
     (après `GroupGuard.Require`), à l'utilisateur ou à un jeton public ; **2 familles ne le font pas** : la console
     d'exploitation (A-031) et la révocation de jetons de rafraîchissement (A-032).
4. **Tests** : les 496 tests d'intégration s'exécutent en superutilisateur (T-001) ; seule une minorité utilise le rôle
   réel. `MultiTenantIsolationTests` (API) et `RowLevelSecurityCoverageTests` couvrent l'isolation par requête et la
   présence de la RLS, pas les portées système.

## 3. Rôles et permissions

- Garde anti-élévation présente à l'attribution d'un rôle et à la création/modification d'un rôle personnalisé
  (`SettingsAndRoles.cs:200`, `UserAssignmentService.GrantAsync`). Absente de la suspension et du retrait (A-010).
- Rôles système non modifiables par l'application (vérifié par lecture), mais modifiables au niveau base par la
  politique `role_isolation` (A-029).
- Permissions d'exploitation `operations.*` accordées aux rôles d'entreprise `manager` et `owner_direction` (A-031).
- `pos.sell` suffit pour régler une créance via `POST /sales/{id}/payments` (A-014).

## 4. Contrôle des identifiants transmis par le client

- `SoldAt` lié depuis le corps de `/pos/checkout` et `/sales` (A-003, exécuté) : un caissier choisit la date de la
  vente et peut l'ajouter à une session close.
- Identifiants d'entité contrôlés par la RLS dans le cas général ; non contrôlés dans `SqlDataCorrectionService`
  (portée système, A-031) et dans la révocation de jetons (A-032).

## 5. Fraude métier

| Scénario | Résultat |
|---|---|
| Vente ajoutée après clôture de caisse | **Possible** (A-003, exécuté) |
| Coupon multiplié par le nombre de lignes | **Possible** (A-012, exécuté) ; la remise coupon échappe au plafond de remise du caissier |
| Écart de caisse fictif imputé au caissier après un remboursement espèces | **Systématique** (A-002, exécuté) |
| Approbation par le demandeur (dépenses, corrections, avances) | Refusée (4 yeux vérifié par lecture) |

## 6. Recommandations de sécurité (ordre)

1. A-031 : sortir `/ops/*` des rôles d'entreprise ou borner chaque opération à l'entreprise active.
2. A-003 : ignorer `SoldAt` hors synchronisation d'un appareil enrôlé.
3. A-009 : réappliquer le contexte RLS à chaque changement de portée ; `SET LOCAL` en transaction ; rôle système distinct.
4. A-010 : garde anti-élévation sur suspension et retrait.
5. A-032, A-024, A-029, A-030, A-011, A-028.
6. Tests de sécurité avec le rôle réel `nexora_app` et deux entreprises de groupes différents.
7. Test d'intrusion externe (non fait, comme le reconnaît le rapport de préparation du projet).
