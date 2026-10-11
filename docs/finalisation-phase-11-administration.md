# Finalisation produit — Phase 11 : Administration / rôles / workflows

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 11 (§26 Administration, §27 Automatisation).
> Branche `feature/admin`. Date : 2026-10-03.

## 1. Travail effectué

Avant cette phase, le centre d'administration renvoyait vers l'API pour créer un rôle, un workflow ou une
automatisation ; on ne pouvait ni modifier les permissions d'un rôle, ni attribuer un rôle à un utilisateur existant ;
l'onglet Rôles était vide en recette (il n'affichait pas les rôles système) ; la suspension passait par `confirm()`.

| Onglet | Après |
|---|---|
| Utilisateurs | création (premier rôle obligatoire), **rôles de chaque utilisateur** : attribution permanente ou **temporaire** avec motif, **retrait motivé**, suspension / réactivation confirmées |
| Rôles | rôles système (lecture, base de copie) et rôles de l'entreprise ; **création** (à partir des permissions d'un autre rôle ou de zéro), **permissions granulaires par module** (filtre, cocher tout un module), **renommer**, **désactiver / réactiver** |
| Workflows | constructeur **QUAND** (opération) / **SI** (seuil de montant) / **ALORS** (nombre d'approbations, rôle approbateur) avec phrase de résultat ; **test** à blanc ; **activation** ; **désactivation** |
| Automatisations | constructeur **QUAND** (fréquence, heure) / **SI** (critère) / **ALORS** (notification) ; aperçu sans effet, exécution, pause / reprise, **historique des exécutions** |
| Paramètres | tableau (valeur effective, origine, sensible) ; modification **typée** (oui/non, liste, nombre) ; **motif obligatoire** pour un paramètre sensible |

Exemple du plan réalisé : rôle « Manager magasin » = ventes (consulter, créer), stock (consulter, transférer),
rapports (consulter), attribué à un utilisateur, effectif immédiatement.

## 2. Fichiers créés

- `database/migrations/V088__permission_labels_fr.sql`
- `frontend/nexora-web/src/admin/RolesPanel.tsx`, `UsersPanel.tsx`, `WorkflowsPanel.tsx`, `AutomationsPanel.tsx`,
  `SettingsPanel.tsx`, `adminRules.ts`, `adminRules.test.ts`

## 3. Fichiers modifiés

- Backend : `SettingsAndRoles.cs` (service des rôles réécrit), `WorkflowAndAutomation.cs`, `ApprovalAndSession.cs`,
  `PermissionService.cs`, `CustomerAccountSupport.cs`, `AdminContracts.cs`, `Abstractions.cs`, `AdministrationController.cs`,
  tests `AdministrationTests.cs`
- Frontend : `admin/AdminCenterPage.tsx` (allégé), `admin/adminApi.ts`, `shared/forms.ts`

## 4. APIs utilisées

Nouvelles : `GET /admin/permissions` (catalogue, avec « vous pouvez l'accorder »), `PUT /admin/roles/{id}`,
`POST /admin/roles/{id}/enable`, `GET /admin/workflows/catalog` (opérations et rôles approbateurs).
Utilisées pour la première fois par l'interface : `POST /admin/roles`, `PUT /admin/roles/{id}/permissions`,
`POST /admin/workflows`, `POST /admin/workflows/{id}/status`, `POST /admin/automations`,
`POST /admin/automations/{id}/status`, `GET /admin/automations/{id}/runs`, `GET|POST /users/{id}/assignments`,
`POST /users/assignments/{id}/revoke`, `GET /admin/settings/definitions`.

### Base de données — V088
Libellés des permissions en français : 125 libellés générés restés en anglais (« Inventory — create ») et les
préfixes de module anglais. Les **codes** ne changent pas. Appliquée sur `nexora` et `nexora_recette`.

## 5. Fonctionnalités connectées — défauts corrigés

1. **Élévation de privilège par les rôles** (sécurité) : on pouvait créer un rôle, ou élargir un rôle existant — y
   compris le sien — avec des permissions que l'on ne détient pas. Les affectations étaient protégées, les rôles non.
   Désormais on n'accorde à un rôle que ce que l'on détient (seuls les ajouts sont contrôlés ; réactiver un rôle aussi).
2. **Un rôle désactivé continuait de donner ses permissions** : le chargement des droits ignorait le statut du rôle.
   Désactiver retire maintenant les droits immédiatement (et la confirmation l'annonce).
3. **Rôle approbateur jamais vérifié** : le rôle choisi dans un workflow était enregistré mais n'importe quel
   approbateur pouvait décider. Il est maintenant imposé à la décision (dérogation de crédit comprise).
4. **Désactiver un workflow ne désactivait pas sa règle** : l'approbation restait exigée par les opérations réelles.
5. **Modifier un workflow actif** le laissait actif, non testé, avec l'ancienne règle toujours appliquée. Un workflow
   actif se désactive avant d'être modifié, puis se reteste.
6. **Le seuil du workflow était ignoré** : la règle créée s'appliquait dès 0, quelle que soit la condition « montant ≥ X ».
7. **Fonctions simulées** : les « actions » et « notifications » d'un workflow n'étaient exécutées nulle part ; 5 types
   d'automatisation sur 8 (rapport planifié, relance fournisseur, aide au rapprochement, archivage, export planifié)
   « s'exécutaient » sans aucun effet. Ils sont refusés à la création, avec un message clair, plutôt que simulés.
8. Déclencheur, rôle approbateur, nombre de niveaux, fréquence d'automatisation : acceptés sans contrôle.
9. **Paramètre sensible** : l'écran envoyait toujours le même motif automatique, ce qui contournait l'obligation
   de motiver la modification.
10. Rôles : liste vide en recette (rôles système non affichés), nombre d'utilisateurs comptant les affectations des
    autres entreprises et les affectations retirées, rôle sans aucune permission accepté.
11. **Formulaires** (tous écrans) : une erreur de champ restait affichée sous un champ déjà corrigé quand une autre
    erreur apparaissait (`shared/forms.ts`).

## 6. Tests ajoutés

- Intégration (`AdministrationTests`, +3) : rôle — élévation refusée à la création et à la modification, rôle système
  non modifiable, nom en double, désactivé sans effet puis réactivé ; workflow — validations, seuil appliqué, workflow
  actif non modifiable, rôle approbateur imposé, désactivation effective ; automatisation sans exécuteur refusée.
  Le test du workflow de remise a été adapté (il utilisait des actions jamais exécutées).
- Frontend (+4) : regroupement des permissions, permissions non accordables, construction / description d'un
  workflow, code technique.

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend | 462 / 462 (dont 3 nouveaux ; les 33 tests administration / identité / sécurité relancés après le dernier correctif) |
| API backend | 186 / 186 (voir §9 : un échec intermittent au premier passage) |
| Frontend Vitest | 243 / 243 |
| Scénario API (direction, gérant, comptable, nouvel utilisateur) | 42 / 42 |
| Navigateur : rôle créé depuis « cashier » + transfert de stock, permission plateforme grisée, attribution temporaire et retrait motivé, workflow QUAND/SI/ALORS testé / activé / désactivé, automatisation de péremption + aperçu, paramètre sensible motivé | réussi, 0 erreur console, 0 requête en échec, 0 boîte native |

## 8. Résultats

L'administrateur crée un rôle « Manager magasin », l'attribue, et l'utilisateur obtient exactement ces droits ; retirer
une permission ou désactiver le rôle prend effet à la requête suivante, sans reconnexion. Un workflow « crédit au-delà
de 50 000 → approbation par la direction » s'applique réellement : une demande à 75 000 l'exige, un gérant ne peut pas
la décider, une demande à 10 000 passe sans approbation, et la désactivation lève l'exigence.

## 9. Problèmes rencontrés

- Le scénario a d'abord trouvé un refus correct mais mal formulé (« Rôle introuvable » pour un rôle système) : corrigé
  en refus explicite « Un rôle système ne se modifie pas ».
- Trois échecs du parcours navigateur venaient du script (utilisateur d'une autre entreprise, notification précédente
  lue, libellé ambigu) ; le quatrième était le vrai défaut n°11.
- **Échec intermittent** au premier passage des tests d'API : `SetupWizardTests` (installation d'un serveur neuf) a vu
  une base neuve non « prête ». Non reproduit ensuite, ni seul (2/2) ni dans la suite complète (186/186). Ce test ne
  touche aucun code de cette phase et signale lui-même sa sensibilité à la charge ; le premier passage tournait en même
  temps que les parcours navigateur de QA. Cause non prouvée : à surveiller.
- Inventaire des boîtes natives : l'administration n'en a plus ; **67 restent dans 9 fichiers** (terrain 6,
  plateforme / facturation 3, sécurité / continuité 25, exploitation 11, BI / intelligence 22), traités dans leurs phases.

## 10. Dette technique restante

- Seul le **seuil de montant** d'un workflow agit sur les opérations réelles (c'est la seule donnée qu'elles transmettent) ;
  seules la dérogation de crédit et les avances / prêts au salarié consultent les règles d'elles-mêmes. Pour les autres
  opérations (dépense, remise, remboursement…), la règle s'applique aux demandes d'approbation déposées ; les brancher sur
  leurs écrans relève de chaque module.
- Le rôle approbateur est vérifié d'après la règle en vigueur au moment de la décision (pas figé à la demande).
- Automatisations : trois types exécutés (relances, stock bas, péremption), qui notifient leur créateur ; pas de
  « créer une tâche » ni d'envoi au client. Le planificateur reste désactivé par défaut (`Scheduler:Enabled`).
- Pas de suppression définitive d'un rôle (désactivation seulement, l'historique des affectations est conservé).
- Affectation au niveau entreprise uniquement depuis l'écran (pas d'affectation par établissement dans l'interface).

## 11. Prochaine phase recommandée

Phase 12 — Terrain.
