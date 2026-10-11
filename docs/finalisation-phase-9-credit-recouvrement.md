# Finalisation produit — Phase 9 : Crédit et recouvrement

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 9 (§23 Crédit et recouvrement).
> Branche `feature/credit`. Date : 2026-10-02.

Cycle visé par le plan : **créance → promesse → échéance → payé / en retard → action → résolution**.

## 1. Travail effectué

Avant cette phase, l'écran « Crédit & recouvrement » affichait le portefeuille, les promesses, les litiges et
les blocages en lecture. On ne pouvait y créer ni promesse, ni litige, ni passage en perte ; quatre actions
passaient par `window.prompt` ; il n'y avait ni agenda des relances ni dossier par client.

| Onglet / écran | Après |
|---|---|
| À traiter | agenda : relances prévues arrivées à date, promesses à échéance, promesses non tenues sans action depuis |
| Portefeuille | balance âgée par client **et par devise**, clic sur un client → dossier de recouvrement |
| Dossier client (tiroir) | créances ouvertes (retard, reste dû, promesse en cours), **action de recouvrement** avec prochaine relance, **promesse de paiement** (créances visées, montant ≤ reste dû, date, canal), **litige** sur une créance, **passage en perte** confirmé, **blocage / déblocage** motivés ; historique des promesses, litiges, actions, blocages et pertes |
| Promesses | filtre par statut, **évaluation** (tenue / partiellement tenue / non tenue), **annulation motivée** |
| Litiges | filtre par statut, **trancher** (issue + résolution écrite) |
| Blocages | actifs ou historique, déblocage motivé |
| Pertes | créances passées en perte, avec leur écriture comptable |

L'échéance d'une promesse est évaluée automatiquement par le planificateur (job `credit.settle_promises`), qui se
déclenche aussi à la main depuis le centre d'exploitation. Plus aucun `window.prompt` dans le crédit ni dans la
fiche client (l'annulation d'un remboursement passe par un dialogue motivé).

> **Rectificatif (phase 10)** : ce rapport affirmait d'abord « plus aucun `window.prompt` dans toute l'application ».
> C'était faux : la vérification ne cherchait que `window.prompt`, alors que plusieurs écrans appellent `prompt(`,
> `alert(` ou `confirm(` sans préfixe (GED, continuité, sécurité, exploitation, terrain, BI…). L'inventaire complet
> est tenu dans le rapport de la phase 10.

## 2. Fichiers créés

- `database/migrations/V086__collections_workflow.sql`
- `frontend/nexora-web/src/credit/CustomerCreditDrawer.tsx`, `CreditDialogs.tsx`, `promiseDraft.ts`, `promiseDraft.test.ts`

## 3. Fichiers modifiés

- Backend : `CollectionsService.cs`, `CreditPortfolioQueries.cs`, `CreditContracts.cs`, `CollectionsInfrastructure.cs`,
  `CollectionsController.cs`, `SchedulerRunner.cs`, tests `CustomerCollectionsTests.cs`
- Frontend : `credit/RecoveryPage.tsx` (réécrit), `credit/creditApi.ts`, `customers/CustomerAccountPage.tsx`,
  `core/apiClient.ts`

## 4. APIs utilisées

Nouvelles : `GET /credit/follow-ups`, `GET /credit/customers/{id}/receivables`, `GET /credit/write-offs`,
`POST /credit/promises/{id}/cancel`.
Utilisées pour la première fois par l'interface : `POST /credit/promises`, `POST /credit/disputes`,
`POST /credit/receivables/{id}/write-off`, `GET|POST /customers/{id}/collection-actions` (depuis le recouvrement),
`POST /ops/jobs/credit.settle_promises/run`.
Renforcées : création / évaluation de promesse, ouverture / résolution de litige, passage en perte, déblocage,
portefeuille et balance âgée (devises).

### Base de données — V086
- Promesse : motif d'annulation (obligatoire si annulée), auteur de la clôture.
- Un seul litige ouvert par créance (index unique partiel).
- Job `credit.settle_promises` enregistré dans le registre d'exploitation.
Appliquée sur `nexora` et `nexora_recette`.

## 5. Fonctionnalités connectées — défauts corrigés

1. **Promesse déclarée « non tenue » avant son échéance** : « Solder » classait non tenue une promesse dont la
   date n'était pas arrivée. Avant l'échéance, seule une promesse entièrement payée peut être close ; à
   l'échéance, le planificateur la classe.
2. Promesse acceptée **datée dans le passé**, **supérieure au reste dû**, sur une créance **soldée, radiée ou en
   litige**, **en double** sur la même créance, ou en **devises mélangées** ; canal inconnu remplacé en silence par
   « téléphone ».
3. **Litige** possible sur la créance **d'un autre client**, pour un montant **supérieur au reste dû**, deux fois
   sur la même créance, sans motif réel ; litige tranché **sans résolution écrite**.
4. **Sortie de litige** : une créance partiellement payée redevenait « ouverte » (les règlements reçus
   disparaissaient de son statut).
5. **Passage en perte d'une créance en litige** possible : désormais refusé tant que le litige n'est pas tranché.
6. **Déblocage** : réactivait un client archivé ou inactif. Seul un client bloqué redevient actif.
7. **Agenda des relances** : une relance prévue restait « à faire » indéfiniment, même après avoir été faite.
   Seule la dernière action du client compte.
8. **Devises additionnées** : portefeuille et balance âgée additionnaient HTG et USD. Une ligne par devise ; la
   balance âgée est en devise de l'entreprise, les autres devises sont présentées à part.
9. **Passage en perte** : l'identifiant d'opération changeait à chaque clic ; il est fixé à l'ouverture du
   dialogue (un double envoi ne radie jamais deux fois — vérifié par rejeu).
10. **Requêtes « annulées » dans le navigateur** (net::ERR_ABORTED) sur toutes les réponses 204 de l'application :
    le client HTTP ne lisait pas le corps vide. Corrigé dans `apiClient.ts` (l'action était bien enregistrée, mais
    le symptôme masquait d'éventuels vrais échecs réseau).

## 6. Tests ajoutés

- Intégration (`CustomerCollectionsTests`, +3) : règles de promesse et évaluation à l'échéance (tenue en avance,
  non tenue échue, annulation) ; règles de litige, statut conservé, perte refusée en litige ; agenda limité à la
  dernière action et déblocage d'un client archivé. 2 tests existants adaptés à la règle corrigée.
- Frontend (+4) : créances éligibles, total de sélection et devises, validation de promesse, appels API
  (filtres, annulation motivée, radiation idempotente).

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend | 457 / 457 (dont 3 nouveaux) |
| API backend | 186 / 186 |
| Frontend Vitest | 236 / 236 |
| Scénario API — scénario métier n°2 du plan (client → crédit → créance → versement → solde) + cycle complet | 34 / 34 |
| Job `credit.settle_promises` lancé depuis le centre d'exploitation | 4 / 4 |
| Navigateur (gérant) : agenda, dossier, action, promesse, litige, blocage, annulation, litige tranché, déblocage, perte | réussi, 0 erreur console, 0 requête en échec, 0 `window.prompt` |

## 8. Résultats

Le cycle du plan est complet : la créance (avec retard) mène à une promesse, l'échéance classe la promesse
d'après les versements réellement affectés, une promesse non tenue entre dans l'agenda, l'action consignée l'en
fait sortir, et la résolution passe par le versement, le litige tranché ou le passage en perte comptabilisé
(Dr créances irrécouvrables / Cr clients). Séparation des tâches vérifiée : le comptable relance et enregistre
des promesses ; litige, blocage et perte relèvent du gérant / de la direction (403 sinon).

## 9. Problèmes rencontrés

- Le planificateur de fond est désactivé par défaut (`Scheduler:Enabled = false`, activé selon le déploiement) :
  la QA a déclenché le job depuis le centre d'exploitation.
- Le test existant « promesse non tenue » encodait le défaut n°1 : il a été réécrit (refus avant échéance,
  non tenue après).

## 10. Dette technique restante

- Litige tranché en faveur du client : l'avoir ou l'annulation du crédit reste à faire depuis la fiche client
  (le message le rappelle) ; pas d'enchaînement automatique.
- Pas de score de risque client explicable, ni d'échéancier de paiement depuis le recouvrement
  (l'échéancier existe à la création d'un crédit).
- Les relances automatiques (politique de relance, phase 14) écrivent dans les notifications, pas dans le
  journal d'actions de recouvrement.
- Délai moyen d'encaissement calculé sur les ventes toutes devises confondues.

## 11. Prochaine phase recommandée

Phase 10 — GED / signatures.
