# Finalisation produit — Phase 14 : Groupe / multi-société

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 14 (§36 Groupe / multi-société).
> Branche `feature/group`. Date : 2026-10-04.

Objectif du plan : compléter les interfaces des **opérations intersociétés**, de la **correspondance des comptes**, de la
**consolidation** et de la **politique groupe**, en respectant strictement « Tenant A ≠ Tenant B, aucune fuite de données ».

## 1. Travail effectué

L'inspection a commencé par l'isolation, exigence première du plan. Elle a révélé une **fuite réelle** : le gérant et le
comptable de Supermarché ABC, rattachés à cette seule société, lisaient le centre de contrôle du groupe et les
consolidations, donc les créances, dettes et états comptables de Pharmacie ABC et Restaurant ABC. Leur rôle de
société porte `group.view`, et une permission accordée dans **une** société valait pour **tout** le groupe.

**Règle désormais appliquée** (fidèle au modèle d'accès existant, qui distingue déjà les rôles attribués à une société
et ceux attribués au groupe) : les droits `group.*` et `consolidation.*` ne viennent que d'un rôle **attribué au niveau
du groupe** (ou de la plateforme), et ils sont vérifiés **sur le groupe visé par la route**. Attribués dans une seule
société, ils restent sans effet. L'administration permet maintenant d'attribuer un rôle « Tout le groupe ».

L'écran Groupe, qui ne montrait qu'un tableau et trois boutons, est réécrit en cinq onglets :

| Onglet | Contenu |
|---|---|
| **Pilotage** | indicateurs (sociétés, intersociétés à rapprocher, consolidations à valider, tâches, créances et dettes), centre de contrôle par société, **tâches de contrôle** (création, responsable choisi parmi les personnes du groupe, échéance, suivi avec motif obligatoire si bloquée ou annulée) |
| **Intersociétés** | opérations (nature, pièces de chaque côté, montant, état, rapprochement, écart) ; **saisie**, **confirmation**, **rapprochement** automatique ou avec contrepartie choisie parmi les seules opérations inverses confirmées |
| **Correspondances** | plan comptable du groupe (création, modification), couverture par société, **rattachement compte local → compte groupe** (seuls les comptes de même type sont proposés, chaque changement crée une version datée) |
| **Consolidation** | création (période, sociétés), exécution / recalcul, points de contrôle, contributions par société et taux, **bilan et résultat consolidés**, balance consolidée, validation, publication, **réouverture motivée** |
| **Politique & sociétés** | devise de consolidation, méthode de conversion (taux de clôture, seule appliquée), tolérance de rapprochement ; sociétés du groupe, **ajout** d'une société prête à travailler, **archivage motivé** |

Aucun `prompt()` / `alert()` / `confirm()`.

## 2. Fichiers créés

- `database/migrations/V090__group_governance.sql`
- `frontend/nexora-web/src/group/GroupSections.tsx`, `src/group/groupApi.test.ts`
- `backend/tests/Nexora.ApiTests/GroupAccessEndpointsTests.cs`

## 3. Fichiers modifiés

- Sécurité : `Application/Abstractions/Services.cs` (droits de groupe dans le profil), `Application/Security/PermissionCatalog.cs`,
  `Infrastructure/Identity/PermissionService.cs`, `Api/Security/PermissionAuthorization.cs`,
  `Infrastructure/Identity/UserAssignmentService.cs`
- Groupe : `Infrastructure/Group/GroupLedgerService.cs`, `IntercompanyService.cs`, `ConsolidationService.cs`,
  `GroupControlAndTasks.cs`, `Application/Group/Abstractions.cs`, `Contracts/Group/GroupContracts.cs`,
  `Api/Controllers/GroupController.cs`
- Tests : `IntegrationTests/GroupConsolidationTests.cs`
- Frontend : `group/GroupPage.tsx`, `group/groupApi.ts` (réécrits), `admin/UsersPanel.tsx`, `admin/adminApi.ts`

## 4. APIs

Nouvelles : `GET /groups/{id}/options` (sociétés, devises, activités, personnes du groupe, méthodes de conversion
appliquées), `GET /groups/{id}/accounts/mappings?companyId=` (comptes locaux et correspondance active).
Étendues : opérations intersociétés (pièces, contrepartie, écart), attribution de rôle (`scopeType: "GROUP"`).
Utilisées pour la première fois par l'interface : plan comptable groupe, correspondances, couverture, politique,
intersociétés (saisie, confirmation, rapprochement), création / réouverture / états de consolidation, tâches (création,
suivi), ajout et archivage de sociétés.

### Base de données — V090
- Référence de pièce intersociétés (`source_document`, `target_document`) : elle était rangée dans la colonne prévue pour
  un **type** de document ; reprise des valeurs existantes.
- Auteur et date de confirmation d'une opération intersociétés ; motif d'archivage d'une société.
- **Journal d'audit** sur les tables de gouvernance, qui n'étaient pas tracées : société, plan comptable groupe,
  correspondances, politique, opérations et rapprochements intersociétés, consolidations, tâches.
Appliquée sur `nexora` et `nexora_recette`.

## 5. Défauts corrigés

**Isolation (Tenant A ≠ Tenant B)**
1. **Fuite vers les sociétés sœurs** : un rôle d'une seule société ouvrait toutes les routes du groupe (voir §1).
2. **Archivage d'une société d'un autre client** : la route d'archivage ne vérifiait pas que la société appartient au
   groupe ; une société indépendante n'était même soumise à aucun contrôle de groupe.
3. **Retrait du rôle de la direction** par un administrateur d'une seule société : il suffisait qu'une société du
   groupe soit dans son périmètre. Un rôle de groupe ne se retire plus que depuis le niveau groupe ; il ne s'attribue
   que par quelqu'un qui détient ces droits sur tout le groupe.
4. Tâche assignable à n'importe quel utilisateur, y compris d'un autre client.

**Exactitude**
5. **Consolidation multidevise impossible** : dès qu'une société n'était pas dans la devise de consolidation,
   l'exécution plantait (date transmise sans fuseau). Le chemin n'avait jamais fonctionné.
6. **Taux de change manquant converti à 1** sans bloquer : une société en HTG pouvait entrer telle quelle dans une
   consolidation en USD, et celle-ci restait validable. Elle ne l'est plus tant que le taux n'est pas saisi.
7. **Date décalée d'un jour** : une opération du 04/10 apparaissait au 03/10 (relue en heure d'Haïti), ce qui
   pouvait aussi la faire sortir de la période consolidée.
8. **Double élimination** : une opération pouvait être rapprochée comme source alors qu'elle était déjà la contrepartie
   d'un autre rapprochement ; une contrepartie choisie n'était contrôlée ni sur son sens ni sur un rapprochement
   existant ; une opération en brouillon se rapprochait. La publication n'éliminait que l'un des deux côtés.
9. **Correspondance de types incompatibles** (un compte de charge rattaché à un compte d'actif groupe) : acceptée, elle
   faussait bilan et résultat ; changer le type d'un compte groupe déjà alimenté aussi.
10. **Politique non enregistrée** : sans ligne existante, la mise à jour renvoyait « succès » sans rien sauver.
11. **Méthode de conversion fictive** : taux moyen et taux historique acceptés, alors que seul le taux de clôture est
    appliqué. Ils sont refusés et l'écran le dit.
12. Montant intersociétés nul ; société archivée encore consolidable ou source d'opérations ; libellé de consolidation
    vide ; période de plusieurs années ; profil de consolidation d'un autre groupe ; nom de société en double dans le
    groupe ; double archivage ; motif d'archivage perdu.
13. **Réattribution d'un rôle retiré** : l'affectation était réactivée mais l'API renvoyait un identifiant inexistant,
    si bien que son retrait par cet identifiant échouait.

## 6. Tests ajoutés

- Intégration (`GroupConsolidationTests`, +6 ; tests existants adaptés au cycle brouillon → confirmée, à la nouvelle
  signature d'archivage et à une échéance future) : droits de groupe
  seulement par un rôle de groupe ; attribution et retrait au niveau groupe (élévation refusée, retrait par un
  administrateur de société refusé, réattribution) ; rapprochement (sens, double élimination, pièces, date) ;
  politique réellement enregistrée, méthode non appliquée refusée, correspondances (types, versions) ; consolidation
  sans taux non validable puis convertie et validée avec le taux ; archivage hors groupe et tâches.
- API (`GroupAccessEndpointsTests`) : un gérant d'une seule société reçoit 403 sur les routes du groupe (imbriquées et
  « à plat ») et n'a plus `group.view` dans `/me` ; la direction et le comptable de groupe y accèdent ; un groupe
  inconnu reste fermé.
- Frontend (+4) : en-tête de périmètre groupe sur chaque appel, rapprochement, choix des contreparties (mêmes règles
  que le serveur), cycle de consolidation.

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend | 468 / 472 — les 4 échecs sont des tests de date (voir §9), sans lien avec cette phase |
| API backend | 187 / 187 (relancée seule ; lancée en même temps qu'une autre suite, elle échouait sur les clones de base) |
| Frontend Vitest | 253 / 253 (54 fichiers) |
| Scénario API (direction, gérant, comptable) | 60 / 60 |
| Navigateur : gérant sans accès ; direction sur les cinq onglets (tâche, deux opérations confirmées puis rapprochées, correspondance modifiée, consolidation créée et calculée, politique, société créée puis archivée) ; rôle « Tout le groupe » attribué puis retiré ; cinq onglets en taille téléphone | réussi, 0 erreur, 0 boîte native, aucun défilement horizontal |

## 8. Résultats

La gouvernance de groupe est utilisable de bout en bout depuis l'interface, et le cloisonnement est réel : on ne voit
le groupe que si l'on a reçu un rôle sur le groupe, ce que seule une personne déjà habilitée au niveau du groupe peut
accorder ou retirer. La consolidation refuse désormais de valider des chiffres faux (taux manquant, incohérence) et ne
peut plus éliminer deux fois la même opération.

## 9. Problèmes rencontrés

- **Tests de date** : exécutés à 23 h à Haïti (déjà le lendemain en UTC), 4 tests échouent — les 3 connus
  (`MarketplaceEcosystemTests.Usage_recorder…`, `GuideFonctionnelTests.A_recorded_expense…`,
  `GuideFonctionnelTests.The_assistant_answers…`) et `BiReportingTests.Objective_set_list_delete…`, de la même famille :
  la vente est datée du lundi en UTC alors que la semaine locale s'arrête le dimanche. Aucun ne touche au code du groupe ;
  le correctif de fond (fuseau de l'entreprise) reste ouvert.
- Un test ajouté supposait que Pharmacie n'avait qu'une écriture en juillet ; d'autres tests de la même collection y
  écrivent aussi. L'assertion compare désormais au total non converti.
- La suite a été interrompue par une mise en veille de la machine (durée affichée de plus de 8 h) ; relancée ensuite.

- Les données de recette n'ont pas de période clôturée ni de compte groupe éliminable de type passif : la consolidation
  de QA reste « rapprochements à faire » avec ses points de contrôle, comportement attendu.
- Deux échecs du scénario venaient de l'encodage des accents passés par `psql` dans le script (la valeur en base était
  juste) ; un troisième était l'état réel laissé par le défaut n°13 lors du passage précédent.
- La limite de 10 connexions par minute a interrompu une sonde : comportement de sécurité attendu.

## 10. Dette technique restante

- Conversion au **taux moyen** et au **taux historique**, écarts de conversion : non appliqués (refusés à la saisie).
- Éliminations limitées aux créances / dettes intragroupe sur un compte d'actif et un compte de passif éliminables ;
  pas d'élimination des ventes / achats internes ni des marges internes.
- Une opération intersociétés en brouillon ne s'annule pas (pas de route) ; la confirmation n'est pas réservée à la
  société destinataire.
- Pas de flux de trésorerie consolidé, ni de grand livre consolidé.
- Le compte d'API de test `gerant.pharmacie.groupe@abc-group.demo` est créé une fois dans la base de développement.

## 11. Prochaine phase recommandée

Phase 15 — Paiements réels / connecteurs.
