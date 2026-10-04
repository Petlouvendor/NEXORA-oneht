# Finalisation produit — Phase 12 : Terrain

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 12 (§34 Terrain).
> Branche `feature/field`. Date : 2026-10-03.

Cycle visé par le plan : **assigné → en route → sur place → terminé → preuve → synchronisé**.

## 1. Travail effectué

Avant cette phase, l'écran Terrain était une vue de superviseur pilotée par `prompt()` : une tournée se créait avec une
seule adresse, une livraison se déclarait « livrée » en tapant un montant, sans preuve ; le livreur n'avait pas de vue
« ma tournée », ni signature, ni photo, ni fonctionnement hors ligne.

| Onglet | Après |
|---|---|
| **Ma tournée** (livreur, pensé téléphone) | arrêts du jour en cartes : client, adresse, téléphone cliquable, commande ; **En route**, **Sur place**, **Livrer**, **Échec** motivé ; livraison = **signature au doigt** et/ou **photo** (compressée), encaissement sur une commande avec choix de la caisse, **position GPS seulement si l'entreprise l'a prévue** ; **hors ligne**, la livraison est gardée sur l'appareil (« en attente de synchronisation ») |
| **Mes interventions** (technicien) | sur place, terminer : points de contrôle, compte rendu, temps passé, signature facultative ; hors ligne également |
| **Supervision** | tournées du jour avec avancement ; **création de tournée** (livreur habilité, véhicule, arrêts depuis les commandes livrables ou une adresse) ; **consultation des preuves** (signature, photo, auteur, heure, position) ; interventions (création, suivi) |
| **Synchronisation** | opérations de l'appareil en attente / envoyées / en conflit / refusées, envoi manuel, envoi automatique au retour du réseau |

Plus aucun `prompt()` / `alert()` dans le terrain ni dans la caisse.

## 2. Fichiers créés

- `database/migrations/V089__field_ops_workflow.sql`
- `frontend/nexora-web/src/mobile/FieldCapture.tsx` (signature, photo, position), `src/shared/syncOperations.ts`

## 3. Fichiers modifiés

- Backend : `DeliveryService.cs` (réécrit), `FieldInterventionService.cs`, `SyncServices.cs`, `MobileContracts.cs`, interface
  `IDeliveryService`, `MobileController.cs`, `SyncController.cs`, tests `MobileFieldOpsTests.cs`, `CustomerCollectionsTests.cs`
- Frontend : `mobile/FieldOpsSections.tsx`, `FieldOpsPage.tsx`, `mobileApi.ts` (réécrits), `mobileApi.test.ts`,
  `shared/offlineQueue.ts`, `pos/offlineStore.ts`, `pos/PosPage.tsx`, `shared/format.ts`, `shared/theme.css`

## 4. APIs utilisées

Nouvelles : `GET /field-ops/options` (agents habilités, commandes livrables, caisses, géolocalisation prévue ou non),
`POST /field-ops/device` (appareil de synchronisation du livreur, sans droit de caisse).
Étendues : arrêts et tournées (client, commande, montants, horodatage de chaque étape, auteur, livreur), interventions
(client, technicien, auteur).
Utilisées pour la première fois par l'interface : `GET /field-ops/my-stops`, `GET /field-ops/interventions/mine`,
`POST /attachments`, `GET /attachments/{id}`, `POST /mobile/sync/push` (opérations terrain).

### Base de données — V089
- Arrêt : heure « en route », heure « sur place », auteur de la conclusion ; intervention : auteur de la conclusion.
- Paramètre d'entreprise **`field_ops.capture_location`** (sensible, désactivé par défaut) : la position n'est enregistrée
  que s'il est activé, et le navigateur demande en plus l'accord de l'utilisateur.
Appliquée sur `nexora` et `nexora_recette`.

## 5. Fonctionnalités connectées — défauts corrigés

1. **Encaissement fantôme** : un montant « encaissé » sur un arrêt sans commande, ou sans caisse indiquée, était enregistré
   sans aucun mouvement d'argent ni écriture. Un encaissement exige maintenant une commande et une caisse.
2. **Livraison sans preuve** : un arrêt se déclarait livré sans signature ni photo. Une preuve est exigée ; les pièces
   jointes sont vérifiées (de cette entreprise, du bon type — une photo n'est pas une signature).
3. **Position enregistrée sans autorisation** : les coordonnées envoyées étaient toujours stockées. Elles ne le sont que si
   l'entreprise l'a prévu (paramètre), et elles sont contrôlées.
4. **Tournée figée « planifiée »** pendant que ses arrêts avançaient : elle passe « en cours » au premier mouvement.
5. **Synchronisation** : l'envoi des opérations hors ligne exigeait le droit de vendre en caisse — un livreur ne pouvait
   pas synchroniser — et ne contrôlait pas le droit de chaque opération : un caissier pouvait conclure une livraison hors
   ligne. Chaque opération est désormais contrôlée selon son type (vente, versement, livraison, intervention).
6. **File hors ligne** : l'expéditeur de la caisse envoyait toute opération comme une « vente ». La traduction est
   commune (`shared/syncOperations.ts`) et chaque écran ne vide que ce qu'il sait envoyer ; une preuve déjà envoyée n'est
   pas renvoyée au nouvel essai.
7. Livreur / technicien non habilité, commande non livrable ou déjà prévue dans une autre tournée, coordonnées hors
   limites, date passée : acceptés sans contrôle. Intervention close sans compte rendu.
8. **Dates en anglais** pour les utilisateurs en créole (`ht-HT`, que les navigateurs ne savent pas formater) :
   « Oct 3, 2026, 8:51 PM ». Repli sur le français d'Haïti.
9. **Boîtes de dialogue débordant de l'écran sur téléphone** (toutes les fenêtres de l'application) : corrigé dans la
   feuille de style commune.

## 6. Tests ajoutés

- Intégration (`MobileFieldOpsTests`, +2) : cycle de livraison (livreur habilité, coordonnées, date, tournée en cours,
  ordre des étapes, preuve exigée et vérifiée, encaissement sans commande refusé, position non prévue / prévue) ;
  synchronisation contrôlée par opération (caissier refusé, livreur accepté) ; intervention (technicien habilité,
  compte rendu exigé, début déduit hors ligne). Le test de rejeu hors ligne fournit maintenant une preuve.
- Frontend (+4) : étapes de l'arrêt, traduction des opérations (jamais un faux type), file filtrée par écran et preuve
  envoyée une seule fois, mise en file réservée aux pertes de réseau ; langue de formatage.

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend | 460 / 464 — voir §9 (heure) ; le 4ᵉ échec, corrigé, repasse |
| API backend | 186 / 186 |
| Frontend Vitest | 244 / 244 |
| Scénario API (gérant, livreur, caissier) | 28 / 28 |
| Navigateur en **taille téléphone (390 px)** : tournée créée par le gérant, livreur en route → sur place → livraison signée ; **coupure réseau** → livraison avec photo et signature gardée sur l'appareil → **retour du réseau** → synchronisée ; preuve consultée par le gérant | réussi, 0 erreur hors la requête coupée volontairement, 0 boîte native |

## 8. Résultats

Le cycle du plan fonctionne de bout en bout, y compris sans réseau : le livreur voit sa tournée sur son téléphone, avance
les étapes, fait signer et photographie ; hors ligne, la livraison attend sur l'appareil et part au retour du réseau,
une seule fois (un second envoi du même arrêt devient un conflit, jamais un doublon). Le superviseur voit l'avancement et
la preuve. La position n'est jamais enregistrée tant que l'entreprise ne l'a pas décidé.

## 9. Problèmes rencontrés

- **Trois tests à date** (`MarketplaceEcosystemTests.Usage_recorder…`, `GuideFonctionnelTests.A_recorded_expense…`,
  `GuideFonctionnelTests.The_assistant_answers…`) échouent entre 20 h et minuit à Haïti : PostgreSQL date en heure
  locale (3 octobre), l'application en UTC (4 octobre). Constaté à 20 h 37 locale (`now()` PostgreSQL vs UTC). Connu et
  documenté ; sans lien avec cette phase. Le correctif de fond (fuseau de l'entreprise) reste ouvert.
- Un quatrième test du même genre, **écrit en phase 9** (retard d'une créance), supposait la date locale : corrigé pour
  calculer le retard sur la même horloge que le service.
- Deux échecs du parcours navigateur venaient du script (signature simulée hors de la zone visible ou pendant un
  défilement) ; leur diagnostic a révélé le vrai défaut n°9.
- Inventaire des boîtes natives : terrain et caisse n'en ont plus ; **61 restent dans 8 fichiers** (plateforme /
  facturation 3, sécurité / continuité 25, exploitation 11, BI / intelligence 22).

## 10. Dette technique restante

- **Pièces utilisées** en intervention : notées, mais pas décomptées du stock (l'écran le dit).
- « En route », « sur place » et « échec » ne s'enregistrent qu'en ligne (le moteur de synchronisation ne rejoue que la
  livraison et l'intervention terminées) ; hors ligne, le livreur passe directement à la livraison.
- Pas de carte ni d'itinéraire ; la position est un point au moment de la preuve, jamais un suivi continu.
- Le profil hors ligne de la tournée (arrêts) n'est pas mis en cache : il faut avoir ouvert « Ma tournée » en ligne
  avant de perdre le réseau, puis l'onglet reste utilisable tant qu'il n'est pas rechargé.
- Notifications sur le téléphone : canal prévu, fournisseur réel non configuré (phase 16).
- Toucher le fond d'une boîte de dialogue la ferme (comportement commun) : sur téléphone, une signature peut être perdue
  par un geste maladroit.

## 11. Prochaine phase recommandée

Phase 13 — Restaurant.
