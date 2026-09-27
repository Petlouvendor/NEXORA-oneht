# NEXORA — PHASE 22 — IDENTITY, RÔLES, PERMISSIONS & MULTI-ENTREPRISES

**Conception détaillée du contrôle d'accès NEXORA V1.0**
Version 1.0

## 1. Objectif

Construire le socle d'identité et d'autorisation permettant à un même utilisateur de gérer une
ou plusieurs entreprises, avec des niveaux d'accès différents selon le groupe, l'entreprise,
l'établissement ou la caisse. Ce module doit être utilisable par une petite entreprise comme par
un groupe complexe.

## 2. Principe fondamental

```
IDENTITÉ
   ↓
RÔLES
   ↓
PERMISSIONS
   ↓
AFFECTATIONS / SCOPES
   ↓
RESSOURCES
   ↓
ACTION AUTORISÉE OU REFUSÉE
```

L'autorisation est toujours décidée côté serveur. L'interface ne fait qu'adapter l'expérience à
ce que le serveur autorise.

## 3. Hiérarchie des périmètres

```
GROUPE
 ├── ENTREPRISE A
 │    ├── ÉTABLISSEMENT 1
 │    ├── ÉTABLISSEMENT 2
 │    └── CAISSES
 ├── ENTREPRISE B
 │    └── ÉTABLISSEMENT
 └── ENTREPRISE C
```

- Une entreprise peut être indépendante.
- Un groupe peut contenir plusieurs entreprises.
- Un utilisateur peut avoir plusieurs affectations.
- Une affectation peut avoir une date de début et de fin.
- Un rôle peut être différent selon l'entreprise.

## 4. Exemples d'accès

```
CAISSIER
→ Entreprise A
→ Caisse A1
→ Vente / encaissement
→ Pas de comptabilité générale

COMPTABLE
→ Entreprise A
→ Comptabilité A
→ Finance A
→ Pas d'accès Entreprise B

COMPTABLE EN CHEF
→ Entreprise A + B + C
→ Comptabilité de chaque entreprise
→ Consolidation du groupe

DIRECTION
→ Groupe
→ Vue financière globale
→ Accès selon politique de l'organisation
```

## 5. Identité utilisateur

- Nom.
- Prénom.
- Email/téléphone selon identifiant retenu.
- Identifiant interne.
- Statut.
- Date création.
- Dernière connexion.
- MFA activée.
- Préférences.
- Langue.
- Fuseau horaire.
- Photo/avatar optionnel.

## 6. Statuts utilisateur

- Invité.
- Actif.
- Suspendu.
- Bloqué.
- Désactivé.
- En attente d'activation.

Une désactivation ne doit pas supprimer l'historique des opérations réalisées par l'utilisateur.

## 7. Authentification

- Identifiant + secret.
- MFA optionnelle/obligatoire selon politique.
- Réinitialisation sécurisée.
- Expiration/session.
- Révocation.
- Verrouillage après comportement suspect.
- Journalisation sécurité.
- Réauthentification pour opérations critiques.

## 8. Sessions

- Session ID interne.
- Utilisateur.
- Device.
- Créée à.
- Expire à.
- Dernière activité.
- Adresse réseau selon politique de sécurité.
- Révoquée à.
- Motif révocation.

## 9. Devices

- Nom terminal.
- Type.
- Utilisateur.
- Entreprise autorisée.
- Dernière activité.
- Statut.
- Clé/device identity selon architecture.
- Révocation.
- POS identifié.

## 10. Rôles

Les rôles sont des regroupements de permissions. Ils ne doivent pas être confondus avec
l'entreprise ou le périmètre d'accès.

- Super administrateur système.
- Administrateur groupe.
- Administrateur entreprise.
- Direction.
- Comptable en chef.
- Comptable.
- Responsable finance.
- Responsable stock.
- Manager.
- Caissier.
- Auditeur.
- Opérateur personnalisé.

## 11. Permissions

```
DOMAIN.ACTION

organization.read
organization.manage
users.read
users.manage
sales.read
sales.create
sales.return
payments.create
inventory.read
inventory.adjust
purchases.create
credit.create
finance.read
finance.manage
accounting.read
accounting.post
accounting.close
consolidation.read
consolidation.manage
audit.read
settings.manage
```

La liste exacte sera complétée par module. Les permissions doivent être suffisamment
granulaires pour éviter de créer des rôles impossibles à adapter.

## 12. Rôles personnalisés

- Créer un rôle.
- Copier un rôle.
- Ajouter/retirer permissions.
- Limiter scope.
- Désactiver rôle.
- Historiser modifications.
- Interdire retrait de permission critique sans autorisation supérieure.

## 13. Affectation

```
USER
 ↓
ASSIGNMENT
 ├── GROUP
 ├── COMPANY
 ├── ESTABLISHMENT
 └── CASH REGISTER
        +
      ROLE
```

- Une affectation représente le droit d'exercer un rôle dans un périmètre.
- Plusieurs affectations possibles.
- Les conflits sont résolus par politique explicite, jamais implicitement.

## 14. Scope effectif

```
GLOBAL
  ↓
GROUP
  ↓
COMPANY
  ↓
ESTABLISHMENT
  ↓
CASH REGISTER
```

Une permission globale ne doit pas être interprétée comme un accès illimité sans règle
explicite.

## 15. Contexte courant

- L'utilisateur choisit le contexte dans lequel il travaille.
- Le contexte est transmis au backend par mécanisme sécurisé.
- Le serveur recalcule les droits effectifs.
- Le frontend affiche toujours le contexte actif.
- Changement de contexte déclenche revalidation et rafraîchissement.

## 16. Sélecteur d'entreprise

- Entreprises accessibles.
- Groupe.
- Établissements.
- Caisses.
- Recherche.
- Favoris.
- Dernier contexte.
- Indication accès lecture seule.

## 17. Isolation des données

```
REQUEST
 ↓
AUTHENTICATE USER
 ↓
RESOLVE SCOPE
 ↓
AUTHORIZE ACTION
 ↓
FILTER RESOURCE
 ↓
EXECUTE
```

- Aucune confiance dans company_id envoyé par client.
- Toutes les requêtes sensibles vérifient le scope.
- Tests automatiques d'isolation.
- Logs d'accès refusés.

## 18. Principe de moindre privilège

- Caissier ne reçoit que les permissions nécessaires.
- Comptable n'accède qu'aux entreprises affectées.
- Manager ne peut pas modifier des paramètres comptables critiques sans droit.
- Administrateur entreprise ne devient pas automatiquement administrateur groupe.
- Accès temporaire possible avec expiration.

## 19. Séparation des responsabilités

- Création fournisseur.
- Validation paiement.
- Approbation dépense.
- Validation écriture.
- Clôture période.
- Réouverture période.
- Publication consolidation.
- Modification plan comptable.

Selon la configuration, une même personne peut ou non cumuler certaines fonctions. Les règles
doivent être configurables et auditables.

## 20. Approbations

```
DEMANDE
 ↓
RÈGLE
 ↓
APPROBATEUR
 ↓
APPROVE / REJECT
 ↓
ACTION
 ↓
AUDIT
```

- Seuil financier.
- Nombre d'approbateurs.
- Rôle requis.
- Entreprise.
- Catégorie.
- Date d'expiration.
- Délégation contrôlée.

## 21. Accès comptable

- Lecture plan comptable.
- Création brouillon.
- Validation écritures selon permission.
- Grand livre.
- Balance.
- Rapports.
- Clôture selon permission.
- Réouverture exceptionnelle.
- Audit.

## 22. Comptable en chef

- Accès à plusieurs entreprises autorisées.
- Vue comptabilité par entreprise.
- Comparaison.
- Contrôles intercompany.
- Préparation consolidation.
- Consolidation selon permission.
- Accès aux anomalies groupe.
- Aucune modification hors scope.

## 23. Direction groupe

- Dashboard groupe.
- Performance entreprises.
- Trésorerie selon droits.
- Créances/dettes.
- Résultat.
- Consolidation.
- Alertes.
- Rapports.
- Lecture seule possible selon rôle.

## 24. Caissier

- Accès caisse affectée.
- Vente.
- Paiement.
- Retour selon permission.
- Ouverture/clôture.
- Consultation limitée.
- Aucune modification comptable directe.
- Accès aux autres entreprises interdit.

## 25. Auditeur

- Lecture contrôlée.
- Audit.
- Historique.
- Rapports.
- Pas de modification métier.
- Scope défini.

## 26. Administrateur entreprise

- Utilisateurs entreprise.
- Affectations.
- Paramètres entreprise.
- Produits/configuration selon permission.
- Caisses.
- Workflows.
- Ne peut pas administrer un groupe sans permission distincte.

## 27. Super administrateur

- Gestion technique globale.
- Configuration système.
- Support.
- Accès exceptionnel.
- Accès métier sensible soumis à audit.
- Compte fortement protégé.

## 28. Autorisation backend

```
Authorize(
  Permission = "sales.create",
  Scope = Company
)

→ vérifier identité
→ vérifier permission
→ vérifier company scope
→ vérifier statut
→ autoriser
```

## 29. Autorisation par ressource

Une permission générale ne suffit pas toujours. L'accès doit également vérifier la ressource
cible.

- Utilisateur peut modifier entreprise A mais pas B.
- Comptable peut lire facture A mais pas B.
- Caissier peut utiliser caisse 2 mais pas caisse 3.
- Manager peut approuver jusqu'à un certain montant.

## 30. Permissions financières sensibles

- Modifier solde initial.
- Créer ajustement caisse.
- Approuver dépense.
- Modifier compte bancaire.
- Enregistrer paiement manuel.
- Modifier crédit client.
- Réouvrir période.
- Modifier écritures validées.
- Publier consolidation.
- Modifier mapping intercompany.

Ces actions doivent demander permissions dédiées et générer des événements d'audit.

## 31. Accès lecture seule

- Entreprise en read-only.
- Comptabilité read-only.
- Reporting read-only.
- Audit read-only.
- Consolidation read-only.
- POS interdit.

## 32. Accès temporaire

- Date début.
- Date fin.
- Motif.
- Créateur.
- Approbateur.
- Expiration automatique.
- Audit.
- Révocation.

## 33. Délégation

- Déléguer une approbation.
- Définir durée.
- Définir périmètre.
- Limiter permissions.
- Historiser.
- Révoquer.

## 34. Sécurité des comptes

- Politique mot de passe/secret selon mécanisme.
- MFA.
- Verrouillage.
- Révocation sessions.
- Détection connexions inhabituelles.
- Notifications sécurité.
- Journalisation.

## 35. Récupération compte

- Demande sécurisée.
- Token à durée limitée.
- Usage unique.
- Réinitialisation.
- Invalidation sessions selon politique.
- Audit.

## 36. Onboarding utilisateur

```
INVITATION
 ↓
ACTIVATION
 ↓
PROFILE
 ↓
ROLE
 ↓
SCOPE
 ↓
MFA
 ↓
READY
```

## 37. Onboarding entreprise

```
CREATE GROUP?
    ↓
CREATE COMPANY
    ↓
CURRENCY / TIMEZONE
    ↓
ESTABLISHMENT
    ↓
CASH REGISTER
    ↓
ADMIN
    ↓
ACCOUNTING / FINANCE SETUP
    ↓
READY
```

## 38. Compte administrateur initial

- Créé par processus contrôlé.
- MFA recommandée.
- Permissions minimales nécessaires.
- Journalisation.
- Possibilité de transfert de responsabilité.

## 39. API Identity

```
POST /api/v1/auth/login
POST /api/v1/auth/logout
POST /api/v1/auth/refresh
POST /api/v1/auth/mfa/verify
POST /api/v1/auth/password/reset
GET  /api/v1/me
GET  /api/v1/me/scopes
GET  /api/v1/me/permissions
```

## 40. API Users

```
GET    /api/v1/users
POST   /api/v1/users
GET    /api/v1/users/{id}
PATCH  /api/v1/users/{id}
POST   /api/v1/users/{id}/suspend
POST   /api/v1/users/{id}/activate
GET    /api/v1/users/{id}/assignments
POST   /api/v1/users/{id}/assignments
```

## 41. API Roles

```
GET    /api/v1/roles
POST   /api/v1/roles
GET    /api/v1/roles/{id}
PATCH  /api/v1/roles/{id}
GET    /api/v1/permissions
POST   /api/v1/roles/{id}/permissions
DELETE /api/v1/roles/{id}/permissions/{permissionId}
```

## 42. API Organisations

```
GET/POST /api/v1/groups
GET/POST /api/v1/companies
GET/PATCH /api/v1/companies/{id}
GET/POST /api/v1/companies/{id}/establishments
GET/POST /api/v1/establishments/{id}/cash-registers
```

## 43. API contexte

```
GET /api/v1/context
POST /api/v1/context/select
GET /api/v1/context/available
```

Le contexte sélectionné est une commodité ; chaque requête sensible doit être autorisée
indépendamment.

## 44. Audit identité

- Login réussi/échoué.
- Logout.
- MFA.
- Réinitialisation.
- Création utilisateur.
- Modification rôle.
- Modification scope.
- Suspension.
- Réactivation.
- Accès refusé.
- Élévation de privilège.
- Modification permission.

## 45. Tests critiques

- Caissier accède uniquement entreprise A.
- Caissier ne voit pas entreprise B.
- Comptable A ne lit pas données B.
- Comptable A+B peut basculer entre A et B.
- Comptable en chef voit groupe autorisé.
- Utilisateur sans permission reçoit 403.
- ID entreprise forcé dans URL ne contourne pas scope.
- Rôle modifié prend effet selon politique.
- Utilisateur suspendu ne peut plus ouvrir session.
- Session révoquée refusée.
- Permission critique auditée.

## 46. Tests de non-régression

- Toutes routes existantes protégées.
- Frontend masque actions non autorisées.
- Backend refuse actions interdites.
- Exports respectent scope.
- Rapports respectent scope.
- Webhooks ne permettent pas de contourner l'autorisation.
- Offline conserve l'identité et le scope du device.

## 47. Performance

- Chargement permissions mis en cache avec invalidation contrôlée.
- Scope résolu efficacement.
- Index sur assignments.
- Éviter requêtes répétées par permission.
- Ne pas charger toutes les entreprises d'un groupe si l'utilisateur n'y a pas accès.

## 48. Audit et conformité

- Journal immuable ou protégé selon architecture.
- Horodatage.
- Utilisateur.
- Scope.
- Action.
- Ressource.
- Résultat.
- Correlation ID.
- Motif pour actions sensibles.

## 49. Critères de sortie

- Identity opérationnelle.
- Authentification opérationnelle.
- Users opérationnels.
- Roles opérationnels.
- Permissions opérationnelles.
- Assignments opérationnelles.
- Scopes groupe/entreprise/site/caisse.
- Sélecteur contexte.
- Isolation testée.
- Caissier testé.
- Comptable testé.
- Comptable en chef testé.
- Direction testée.
- Audit activé.
- Approbations définies.
- Accès temporaire défini.
- API documentée.
- Tests automatisés.

## 50. Étape suivante

Passer à la **Phase 23 — Catalogue produits, services, prix, taxes, unités et codes-barres**.
Cette phase construira la base commerciale commune à la pharmacie, au supermarché, au
restaurant, au commerce de gros, aux services et aux autres entreprises.

---

**NEXORA V1.0 — PHASE 22 VALIDÉE → PHASE 23 : CATALOGUE & PRODUITS**
