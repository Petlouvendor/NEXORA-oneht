# Finalisation produit — Phase 17 : Sécurité

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 17 (§37 Sécurité, §38 Refresh token).
> Branche `feature/security`. Date : 2026-10-05.

Exigences : ne jamais diminuer la sécurité existante ; compléter réauthentification, MFA, sessions, refresh tokens,
expiration, audit, permissions, limitation de débit, validation ; confirmation forte selon le risque pour
SUPPRESSION, PAIEMENT, ANNULATION, VALIDATION COMPTABLE, CHANGEMENT DE PERMISSION, ACCÈS AUX DONNÉES SENSIBLES ;
cycle connexion → jeton d'accès → expiration → jeton de rafraîchissement → nouveau jeton d'accès, avec rotation,
révocation, expiration, déconnexion et session compromise.

## 1. Travail effectué

**État trouvé** :
- la migration V078 (refresh tokens) **n'était utilisée par aucune ligne de code** ; le jeton d'accès durait
  60 minutes, puis l'utilisateur était déconnecté ;
- la **réauthentification forte existait mais n'était exigée par aucune action** ; elle ne demandait que le mot de
  passe, même quand la double authentification était active, et ses essais n'étaient ni comptés ni limités ;
- la « durée de session » de la politique était enregistrée mais jamais appliquée ; la session était créée « 12 h »
  en dur, et son expiration n'était pas vérifiée ;
- la règle « double authentification obligatoire pour ces rôles » ignorait les rôles attribués au groupe :
  **la direction y échappait** ;
- aucune interface pour la politique de sécurité ; dix boîtes natives `prompt()` dans la page Sécurité.

**Jetons et sessions** (`auth_refresh_token`, V093) :

| Étape | Comportement |
|---|---|
| Connexion | jeton d'accès de **15 minutes** portant l'identifiant de session (`sid`) ; jeton de rafraîchissement opaque dans un cookie **HttpOnly, SameSite=Strict, limité à `/api/v1/auth`** (jamais lisible par la page) ; seul son SHA-256 est stocké |
| Expiration | le client renouvelle le jeton de façon transparente (un seul renouvellement pour toutes les requêtes en cours, puis nouvel essai) |
| Rotation | chaque renouvellement consomme le jeton et en émet un nouveau |
| Session compromise | un jeton déjà consommé présenté de nouveau ferme **toutes** les sessions de l'utilisateur et l'inscrit au journal ; deux onglets qui renouvellent au même instant ne sont pas pris pour un vol |
| Inactivité | au-delà du délai de la politique (60 min par défaut), plus de renouvellement |
| Durée maximale | 12 h (`Security:SessionMaxHours`) : au-delà, même le jeton d'accès en cours est refusé |
| Révocation / déconnexion | la session entière tombe (jetons d'accès de tous ses renouvellements, jetons de rafraîchissement) ; déconnexion possible même avec un jeton d'accès expiré (`POST /auth/logout`, par le cookie) |

**Confirmation forte par niveau de risque** : niveau ordinaire = confirmation avec motif (déjà en place) ; niveau
critique = réauthentification (mot de passe, + code de double authentification s'il est actif), valable 10 minutes
(politique) **pour une catégorie** et la session, 5 échecs en 15 minutes verrouillent. **41 actions** protégées :

| Catégorie | Actions |
|---|---|
| Changement de permission | rôles (création, modification, permissions, activation), attribution / retrait de rôle, création d'utilisateur, accès d'urgence (demande, décision), clés d'API (création, rotation, révocation) |
| Validation comptable | clôture / réouverture de période, clôture d'exercice, contre-passation, publication / réouverture de consolidation, approbation et clôture de paie |
| Paiement | paiement de paie, paiements fournisseurs, exécution de remboursement client, remboursement en ligne, paiement sur compte, remboursement de note de frais |
| Annulation | annulation de versement client, de facture, de document, de paie, de paiement de salaire |
| Suppression | purge, approbation de restauration, retrait d'une société de la plateforme, archivage d'une société |
| Données sensibles | identifiants bancaires et fiscaux des employés |

Dans l'interface, toute action critique ouvre une fenêtre « Confirmation de sécurité », puis l'action est
**rejouée automatiquement** ; aucune modification n'a été nécessaire écran par écran.

**MFA** : la règle couvre les rôles de groupe ; nouveau réglage « **Imposer la double authentification pour les
actions critiques** » (désactivé par défaut : la règle restait indicative depuis la phase 47 — le rendre bloquant
d'office aurait bloqué toute action critique des comptes de direction) ; si la double authentification est active,
son code est toujours exigé.

**Politique de sécurité** (nouvel onglet) : inactivité, validité de la confirmation forte, rôles soumis à la double
authentification, imposition ; durées bornées.

## 2. Fichiers créés

- `database/migrations/V093__refresh_tokens_step_up.sql`, `V094__security_policy_enforce_mfa.sql`
- `backend/src/Nexora.Api/Security/StepUpFilter.cs`
- `backend/tests/Nexora.ApiTests/SessionSecurityEndpointsTests.cs`, `StepUpTestHelper.cs`,
  `backend/tests/Nexora.IntegrationTests/StepUpMfaTests.cs`
- `frontend/nexora-web/src/auth/StepUpDialog.tsx`, `src/security/SecurityPolicyPanel.tsx`,
  `src/shared/PromptDialog.tsx`, `src/shared/TopLayer.tsx`, `src/core/sessionFlow.test.ts`

## 3. Fichiers modifiés

- Backend : `AuthController`, `MeController`, `SecurityController`, `UsersController` et les contrôleurs des actions
  critiques (attribut `RequireStepUp`), `TenantResolutionMiddleware`, `Program.cs` (limite « refresh »),
  `appsettings.json` (15 min), `JwtTokenService`, `JwtOptions`, `AccessTokenIssuer`, `ApprovalAndSession.cs`
  (sessions, rafraîchissement, confirmation forte, politique), `MfaService`, contrats et interfaces
- Tests : `MfaAuthenticationTests`, `GroupAccessEndpointsTests`, `RecetteEndpointsTests`, trois tests d'API
  adaptés à la confirmation forte
- Frontend : `core/apiClient.ts`, `auth/AuthContext.tsx`, `auth/accountApi.ts` (+ test), `app/AppLayout.tsx`,
  `security/SecurityPage.tsx`

## 4. APIs

Nouvelles : `POST /auth/refresh` (cookie, rotation), `POST /auth/logout` (par le cookie).
Modifiées : `POST /auth/login` et `/auth/mfa/verify` (session + cookie, `sessionExpiresAt`), `POST /me/logout`
(session entière), `POST /security/step-up` (catégories, code MFA, verrouillage ; mot de passe faux = 422),
`GET/PUT /security/policy` (`enforceMfaForCritical`, bornes). 41 actions répondent
`403 security.step_up_required` (avec `purpose`) sans en-tête `X-Step-Up-Token` valide.

### Base de données — V093, V094
`auth_refresh_token` (chaîne de rotation, hachage seul) ; retrait des colonnes V078 jamais utilisées ;
catégories de confirmation, session et méthode du défi ; `step_up_failure` ; audit des défis ;
`security_policy.enforce_mfa_for_critical`. Appliquées sur `nexora` et `nexora_recette`.

## 5. Défauts corrigés

1. Refresh tokens absents (V078 inutilisée) → cycle complet avec rotation et détection de vol.
2. **Aucune action critique ne demandait de confirmation forte** → 41 actions protégées par catégorie.
3. Confirmation forte sans second facteur ni limite d'essais → code MFA exigé s'il est actif, verrouillage.
4. **Direction exemptée de la règle MFA** (rôle de groupe) → couverte.
5. Durée de session de la politique ignorée, expiration de session non vérifiée → inactivité et durée maximale.
6. Révoquer une session ne coupait que le jeton enregistré → tous les jetons de la session (claim `sid`).
7. Déconnexion impossible avec un jeton expiré (le cookie survivait) → `POST /auth/logout`.
8. Un mot de passe faux à la confirmation aurait été pris pour une session expirée (401) → 422.
9. La confirmation forte partageait la limite de 10 connexions/min par adresse → limite dédiée, plus large.
10. Politique de sécurité sans interface, durées non bornées.
11. Dix boîtes natives `prompt()` dans la page Sécurité → fenêtres de l'application (composant réutilisable).
12. Fenêtre ouverte par-dessus un tiroir inutilisable (voile du tiroir au-dessus) → couche supérieure.

## 6. Tests ajoutés

- API (+3) : rotation, onglets simultanés, réutilisation → toutes les sessions fermées ; déconnexion, inactivité,
  durée maximale, jeton inconnu ; confirmation forte (mot de passe faux, catégorie, verrouillage). Comptes dédiés.
- Intégration (+2) : second facteur exigé, règle MFA des rôles de groupe, imposition, catégorie ; bornes de la
  politique. Tests existants adaptés (règle MFA de groupe, confirmation forte).
- Frontend (+5, 2 adaptés) : renouvellement unique puis nouvel essai, pas de renouvellement sur `/auth/`, déconnexion
  si impossible, confirmation forte rejouée et réutilisée, annulation, 403 ordinaire sans confirmation ; déconnexion
  par le cookie.

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 (une simulation adaptée à la nouvelle signature du jeton) |
| Intégration backend | 479 / 479 (un premier passage avait échoué en bloc sur les comptes clients, non reproduit : relancés seuls 22 / 22, puis suite complète verte) |
| API backend | 190 / 190 |
| Frontend Vitest | 264 / 264 (57 fichiers) |
| Scénario API | 36 / 36 |
| Navigateur : attribution de rôle → confirmation forte (mot de passe faux puis juste) au-dessus du tiroir → action rejouée ; retrait sans nouvelle demande ; **jeton expiré → un renouvellement → page affichée sans déconnexion** ; politique ; téléphone | réussi, 0 erreur, 0 boîte native, cookie invisible pour la page |

## 8. Résultats

L'utilisateur reste connecté tant qu'il travaille (jeton de 15 minutes renouvelé seul) et l'est au plus 12 heures ;
inactif, il est déconnecté. Un jeton volé et réutilisé ferme toutes ses sessions. Chaque action à risque demande
une confirmation d'identité adaptée, une fois pour quelques minutes par type d'action.

## 9. Problèmes rencontrés

- Rendre la double authentification bloquante d'office aurait contredit la décision de la phase 47 et bloqué
  les comptes de direction en recette : réglage par entreprise, désactivé par défaut.
- Premier passage du test de renouvellement trop court (tolérance d'horloge de 30 s) : refait avec 100 s.
- Un test ajouté modifiait le mot de passe du compte partagé du fixture : rétabli en fin de test.

## 10. Dette technique restante

- Clients natifs (application mobile) : le jeton de rafraîchissement est uniquement en cookie (adapté au
  navigateur) ; une variante pour les applications natives reste à prévoir.
- Le premier appel d'une action critique part sans confirmation (le client ne connaît pas la catégorie à l'avance) :
  une requête refusée de plus, sans effet.
- 51 boîtes natives restent dans 6 fichiers (BI, continuité, exploitation, intelligence, plateforme, facturation) ;
  le composant `usePrompt` est prêt pour les remplacer (phase 20).
- Pas de WebAuthn / clé de sécurité ; double authentification par application TOTP seulement.

## 11. Prochaine phase recommandée

Phase 18 — Production readiness.
