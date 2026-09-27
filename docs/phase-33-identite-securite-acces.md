NEXORA
PHASE 33 — IDENTITÉ, SÉCURITÉ & GESTION DES ACCÈS
IAM multi-entreprises, authentification, permissions, appareils & audit — NEXORA V1.0
Version 1.0

## 1. Objectif

Construire le système d'identité et de sécurité de NEXORA afin de protéger les données, séparer strictement les entreprises et permettre à une même personne d'avoir des responsabilités différentes selon son périmètre. Le modèle doit fonctionner pour une petite entreprise comme pour un groupe multi-entreprises.

## 2. Principe de sécurité

```
UTILISATEUR
   ↓
IDENTITÉ
   ↓
AUTHENTIFICATION
   ↓
RÔLE
   ↓
PERMISSIONS
   ↓
SCOPE
   ↓
ACTION
   ↓
AUDIT
```

- L'authentification ne donne pas automatiquement l'accès aux données.
- Chaque requête sensible doit être autorisée côté serveur.
- Le scope entreprise/établissement doit être vérifié indépendamment de l'interface.
- Les actions critiques sont journalisées.
- Le principe du moindre privilège est appliqué.

## 3. Identité utilisateur

- Identifiant unique.
- Nom.
- Email/téléphone selon configuration.
- Statut.
- Langue.
- Fuseau horaire.
- Photo/avatar optionnel.
- Préférences.
- Date création.
- Dernière activité.
- Métadonnées de sécurité minimales.

## 4. Comptes utilisateurs

- Invité.
- Actif.
- Suspendu.
- Désactivé.
- Verrouillé temporairement.
- Suppression logique selon politique.
- Réactivation contrôlée.

## 5. Création utilisateur

```
INVITATION
 ↓
VÉRIFICATION
 ↓
ACTIVATION
 ↓
RÔLE
 ↓
SCOPE
 ↓
ACCÈS NEXORA
```

- Invitation par administrateur.
- Expiration invitation.
- Activation contrôlée.
- Pas de compte partagé.

## 6. Authentification

- Email + mot de passe.
- Téléphone selon configuration.
- SSO/OIDC lorsque disponible.
- Authentification locale pour mode local.
- Session sécurisée.
- Réauthentification pour actions critiques.

## 7. Mot de passe

- Hash robuste côté serveur.
- Jamais stocké en clair.
- Politique configurable.
- Protection contre essais répétés.
- Réinitialisation par procédure sécurisée.
- Expiration uniquement si politique de sécurité le justifie.

## 8. MFA

- TOTP.
- Passkey/WebAuthn lorsque disponible.
- Code de récupération.
- MFA obligatoire pour rôles sensibles.
- MFA obligatoire configurable par entreprise.
- Enrôlement contrôlé.

## 9. Récupération compte

- Demande sécurisée.
- Vérification identité.
- Lien/token à durée limitée.
- Réinitialisation.
- Révocation des sessions si nécessaire.
- Journalisation.

## 10. Sessions

- Session unique ou multiple selon politique.
- Expiration.
- Inactivité.
- Révocation.
- Liste appareils.
- Dernière activité.
- Réauthentification.

## 11. Appareils

- Appareil reconnu optionnel.
- Nom appareil.
- Dernière connexion.
- Plateforme.
- Révocation.
- Appareil de confiance avec durée limitée.
- Aucune confiance permanente implicite.

## 12. Gestion des rôles

- Rôles système.
- Rôles personnalisés.
- Permissions granulaires.
- Rôle par entreprise.
- Rôle par établissement.
- Plusieurs rôles pour un utilisateur selon contexte.

## 13. Exemple rôles

```
CAISSIER
→ POS + CAISSE
→ ENTREPRISE A

COMPTABLE
→ COMPTABILITÉ + FINANCE
→ ENTREPRISE B

COMPTABLE EN CHEF
→ COMPTABILITÉ
→ A + B + C

ADMINISTRATEUR GROUPE
→ ADMINISTRATION
→ SCOPE GROUPE
```

## 14. Permissions

- Voir.
- Créer.
- Modifier.
- Valider.
- Annuler.
- Supprimer logiquement.
- Exporter.
- Payer.
- Rembourser.
- Clôturer.
- Réouvrir.
- Administrer.
- Partager.
- Configurer.

## 15. Permissions sensibles

- Modifier limite crédit.
- Autoriser vente crédit hors limite.
- Remboursement.
- Annulation facture.
- Modification compte bancaire fournisseur.
- Paiement fournisseur.
- Radiation créance.
- Réouverture période comptable.
- Modification plan comptable.
- Consolidation.
- Gestion sécurité.

## 16. RBAC + ABAC

NEXORA combine les rôles (RBAC) avec des règles de contexte (ABAC) lorsque nécessaire.

```
AUTORISER =
ROLE
+ PERMISSION
+ ENTREPRISE
+ ÉTABLISSEMENT
+ OBJET
+ CONTEXTE
+ RÈGLE
```

## 17. Scopes

- Groupe.
- Entreprise.
- Établissement.
- Département.
- Caisse.
- Entrepôt.
- Projet.
- Données spécifiques.
- Lecture seule ou écriture.

## 18. Utilisateur multi-entreprises

- Une identité unique.
- Plusieurs affiliations.
- Rôle différent par entreprise.
- Scope indépendant.
- Changement de contexte explicite.
- Audit de l'entreprise active.

## 19. Comptable en chef

- Accès à plusieurs entreprises autorisées.
- Comptabilité multi-entreprises.
- Consolidation si autorisée.
- Finance selon permission.
- Pas d'accès implicite aux données opérationnelles.

## 20. Séparation stricte

```
ENTREPRISE A
  ↓
DONNÉES A

ENTREPRISE B
  ↓
DONNÉES B

GROUPE
  ↓
VUE CONSOLIDÉE AUTORISÉE
```

- Isolation logique stricte.
- Tests automatisés d'isolation.
- Aucun filtre de sécurité uniquement côté frontend.

## 21. Contexte actif

- Entreprise active.
- Établissement actif.
- Caisse active.
- Entrepôt actif.
- Affichage clair du contexte.
- Confirmation avant action sensible dans un autre scope.

## 22. Impersonation support

- Support peut demander une session d'assistance.
- Consentement/autorisation.
- Durée limitée.
- Bannière visible.
- Actions entièrement auditées.
- Interdiction d'accès silencieux.

## 23. Autorisation temporaire

- Élévation de privilège temporaire.
- Motif.
- Durée.
- Approbateur.
- Actions limitées.
- Expiration automatique.
- Audit.

## 24. Approbation à deux personnes

- Préparation par utilisateur A.
- Validation par utilisateur B.
- Impossible de valider sa propre action lorsque séparation requise.
- Seuil configurable.

## 25. Sécurité paiement

- Réauthentification.
- MFA selon risque.
- Validation deuxième personne selon montant.
- Bénéficiaire contrôlé.
- Idempotence.
- Audit.

## 26. Sécurité caisse

- PIN personnel optionnel.
- Verrouillage écran.
- Changement opérateur.
- Clôture protégée.
- Actions sensibles nécessitent permission.

## 27. Sécurité POS

- Session caisse.
- Utilisateur identifié.
- Terminal identifié.
- Mode offline contrôlé.
- Synchronisation authentifiée.
- Aucune confiance dans les montants envoyés par client.

## 28. Sécurité API

- Authentification forte.
- Autorisation serveur.
- Validation entrées.
- Rate limiting.
- Idempotency keys.
- Protection replay.
- Logs sécurité.
- Tokens courts.
- Rotation secrets.

## 29. Tokens

- Access token court.
- Refresh token protégé.
- Rotation.
- Révocation.
- Détection réutilisation selon mécanisme.
- Stockage sécurisé côté client.

## 30. CSRF / XSS / injection

- Protection CSRF selon mécanisme d'authentification.
- CSP.
- Encodage sortie.
- Validation entrée.
- Requêtes paramétrées.
- Protection XSS.
- Protection SQL injection.
- Validation fichiers.

## 31. Sécurité données

- Chiffrement en transit.
- Chiffrement au repos selon architecture.
- Secrets séparés.
- Clés protégées.
- Sauvegardes chiffrées.
- Accès minimisé.
- Données sensibles masquées.

## 32. Données personnelles

- Collecte minimale.
- Finalité.
- Accès contrôlé.
- Export selon droits/politique.
- Suppression/anonymisation lorsque légalement applicable.
- Journalisation des accès sensibles.

## 33. Audit sécurité

- Connexion réussie.
- Échec connexion.
- MFA.
- Réinitialisation.
- Création utilisateur.
- Modification rôle.
- Modification scope.
- Élévation privilège.
- Révocation.
- Action sensible.
- Export.
- Impersonation.

## 34. Journal d'audit

```
TIMESTAMP
USER
IP / DEVICE CONTEXT SELON POLITIQUE
ACTION
RESOURCE
OLD VALUE / NEW VALUE SI PERTINENT
RESULT
CORRELATION ID
```

- Immuable ou fortement protégé.
- Rétention configurable.
- Recherche.
- Export contrôlé.

## 35. Détection anomalies

- Échecs connexion répétés.
- Connexion inhabituelle.
- Multiples appareils.
- Élévation fréquente.
- Exports massifs.
- Accès hors scope.
- Actions sensibles inhabituelles.
- Tentatives répétées.

## 36. Alertes sécurité

- Compte verrouillé.
- MFA désactivé.
- Rôle critique modifié.
- Nouvel appareil.
- Export sensible.
- Tentative accès interdite.
- Modification coordonnées bancaires.
- Réouverture comptable.

## 37. Politique par entreprise

- MFA obligatoire.
- Durée session.
- Permissions minimales.
- Seuil approbation.
- Appareils de confiance.
- Politique mot de passe.
- Export.
- Accès local/cloud.

## 38. Administration groupe

- Gestion entreprises.
- Gestion utilisateurs.
- Politiques globales.
- Rôles groupe.
- Audit groupe.
- Sécurité.
- Sans accès automatique aux données métier.

## 39. Administrateur entreprise

- Utilisateurs entreprise.
- Rôles locaux.
- Scopes.
- Paramètres sécurité.
- Sessions.
- Audit local.
- Ne peut pas sortir de son entreprise sauf permission groupe explicite.

## 40. Mode local

- Comptes locaux.
- Authentification locale.
- MFA selon capacité.
- Stockage sécurisé.
- Journal audit.
- Administrateur local.
- Synchronisation d'identités optionnelle.

## 41. Mode cloud

- IAM centralisé.
- SSO optionnel.
- MFA.
- Gestion appareils.
- Audit central.
- Politiques groupe.

## 42. Mode hybride

- Identité locale + identité centrale.
- Synchronisation contrôlée.
- Cache de permissions minimal.
- Révocation prioritaire.
- Gestion offline des autorisations avec durée limitée.

## 43. Permissions offline

- Seules permissions préalablement autorisées peuvent être utilisées.
- Actions à haut risque peuvent être interdites offline.
- Expiration du cache.
- Synchronisation auditée.
- Conflits signalés.

## 44. Cycle utilisateur

```
INVITATION → ACTIVATION → RÔLE → SCOPE
      ↓
UTILISATION → AUDIT
      ↓
SUSPENSION / MODIFICATION / RÉVOCATION
```

## 45. API principales

```
POST /api/v1/auth/login
POST /api/v1/auth/logout
POST /api/v1/auth/refresh
POST /api/v1/auth/mfa/enroll
POST /api/v1/auth/mfa/verify
POST /api/v1/auth/password/reset
GET  /api/v1/users
POST /api/v1/users/invitations
GET  /api/v1/roles
POST /api/v1/roles
GET  /api/v1/permissions
GET  /api/v1/access-policies
GET  /api/v1/sessions
POST /api/v1/sessions/{id}/revoke
GET  /api/v1/devices
POST /api/v1/devices/{id}/revoke
GET  /api/v1/audit/security
POST /api/v1/elevations
POST /api/v1/impersonation-requests
```

## 46. Événements

```
UserInvited
UserActivated
UserSuspended
LoginSucceeded
LoginFailed
MfaEnabled
MfaDisabled
SessionRevoked
RoleChanged
ScopeChanged
PrivilegeElevated
ImpersonationStarted
ImpersonationEnded
AccessDenied
SecurityAlertCreated
```

## 47. Tests d'autorisation

- Caissier entreprise A → A uniquement.
- Caissier A → B refusé.
- Comptable A → comptabilité A.
- Comptable A → ventes B refusé si non autorisé.
- Comptable en chef → A+B autorisés.
- Groupe → consolidation autorisée.
- Admin entreprise → autre entreprise refusée.

## 48. Tests sécurité

- Brute force.
- Mot de passe faible.
- Reset token expiré.
- Replay token.
- Session révoquée.
- MFA invalide.
- CSRF.
- XSS.
- SQL injection.
- IDOR.
- Scope manipulé.
- Export non autorisé.
- Impersonation expirée.
- Élévation expirée.

## 49. Tests multi-entreprises

- Même utilisateur avec rôles différents.
- Changement entreprise active.
- API avec mauvais tenant.
- Rapport groupe.
- Consolidation.
- Offline.
- Synchronisation.
- Audit contexte.

## 50. Tests audit

- Chaque action critique est enregistrée.
- Ancienne/nouvelle valeur quand nécessaire.
- Identité auteur.
- Scope.
- Résultat.
- Corrélation.
- Impossibilité de masquer une action par modification frontend.

## 51. Critères de sortie

- Utilisateurs.
- Invitations.
- Authentification.
- MFA.
- Récupération.
- Sessions.
- Appareils.
- RBAC.
- ABAC.
- Scopes.
- Multi-entreprises.
- Comptable en chef.
- Impersonation contrôlée.
- Élévation temporaire.
- Double approbation.
- Sécurité caisse/POS.
- Sécurité API.
- Protection données.
- Audit.
- Détection anomalies.
- Alertes.
- Politiques entreprise.
- Local.
- Cloud.
- Hybride.
- Offline.
- Tests isolation.
- Tests sécurité.
- API.

## 52. Étape suivante

Passer à la Phase 34 — Notifications, communication et automatisations. Elle couvrira emails, SMS, notifications internes, WhatsApp lorsque intégrable, rappels, relances, événements métier et automatisations configurables, avec respect des permissions et du périmètre entreprise.

NEXORA V1.0 — PHASE 33 VALIDÉE → PHASE 34 : NOTIFICATIONS & AUTOMATISATIONS
