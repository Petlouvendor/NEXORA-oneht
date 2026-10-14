# Corrections issues de l'audit technique intégral (2026-10)

> Suivi, étape par étape, des corrections du plan `audit-nexora-one/10-plan-de-correction.md`.
> Pour l'équipe technique et la personne qui validera la recette. Identifiants : ceux de
> `audit-nexora-one/09-anomalies-et-priorites.md`.

## Étape 0 — Compilation et isolation des tests d'API (A-001, T-002)

**A-001.** La copie de travail retirait la référence `Nexora.Api` du projet `Nexora.ApiTests` (modification non
commitée) : la solution ne compilait plus (75 erreurs). Référence restaurée ; `dotnet build Nexora.sln` : 0 erreur.

**T-002.** L'infrastructure lisait la chaîne de connexion à l'enregistrement des services. Une chaîne déclarée ensuite
par une fabrique de tests (`ConfigureAppConfiguration`) était ignorée : les tests d'API écrivaient toujours dans la base
de développement `nexora`, qui sert aussi de modèle aux tests d'intégration. La chaîne est désormais lue à la
construction du contexte (`IOptions<NexoraDbOptions>`), comme le faisait déjà le verrou du planificateur.

**Test.** `TestDatabaseTargetTests` : une fabrique qui déclare une autre base obtient un contexte connecté à cette base
(aucun accès réseau).

## Étape 1 — Solde des comptes financiers sans perte sous concurrence (A-018, CRITIQUE)

**Défaut.** `AdjustBalanceAsync`, seul écrivain de `financial_account.current_balance`, lisait le compte, ajoutait le
montant en mémoire puis écrivait la valeur calculée. Deux opérations simultanées sur un même compte (une vente et un
transfert, que des verrous de numérotation différents ne sérialisent pas) perdaient une mise à jour, sans erreur :
525 HTG perdus sur 20 opérations lors de l'audit. Le tableau de bord, le cockpit de trésorerie et le contrôle de solde
des transferts lisent ce solde.

**Correction.** Ajout atomique en base (`current_balance = current_balance + montant`) ; la ligne reste verrouillée
jusqu'à la fin de la transaction de l'appelant. L'entité éventuellement suivie par le contexte est réalignée pour
qu'EF ne réécrive pas l'ancien solde.

**Reprise de l'existant.** V100 réaligne les comptes déjà en écart sur la définition du solde (ouverture + somme des
transactions, qui n'ont jamais été perdues). Aucun écart n'existait sur `nexora` ni `nexora_recette` lors de l'audit.

**Tests.** `TreasuryConcurrencyTests` : 12 ajouts simultanés sur un même compte → solde 12 (l'ancien code donnait **1**,
vérifié en le rétablissant le temps du test) ; transferts simultanés partageant un compte → solde stocké = ouverture +
Σ transactions sur les trois comptes. Non-régression : moteur, trésorerie, cockpit, comptes clients, paie, caisse
(70 tests).

**À faire hors code.** Appliquer V100 sur `nexora` et `nexora_recette` (`Nexora.Api.dll migrate`) : refusé à l'outil
pendant la correction, en attente de l'accord du propriétaire.

## Étape 2 — Retours : remboursement espèces dans la session de caisse, contrôles sous verrou (A-002 CRITIQUE, A-005)

**Défaut (A-002).** Un retour remboursé en espèces sortait de la trésorerie (écriture et transaction) sans aucun
mouvement dans la session de caisse : le type `sale_refund` existait dans la base mais n'était jamais écrit. À la
clôture, l'attendu ignorait le remboursement : faux manquant imputé au caissier, puis l'écart comptabilisé (charge
6580) faisait baisser la caisse une seconde fois. Constaté lors de l'audit : vente 75, retour 75, clôture comptée 0 →
écart −75.

**Défauts (A-005).** Quantité retournable contrôlée hors transaction et sans verrou (deux retours simultanés pouvaient
dépasser la vente) ; verrou `FOR UPDATE` sur la créance pris hors transaction, donc relâché aussitôt ; remboursement
espèces accepté sans compte (l'écriture créditait le compte d'attente, aucune trésorerie ne bougeait) ; indicateur
« remis en stock » toujours vrai.

**Correction.**
- Remboursement espèces : compte de décaissement obligatoire ; s'il s'agit du compte de la caisse de la session, la
  session doit être ouverte et un mouvement `sale_refund` (montant négatif) est écrit dans la même transaction que le
  retour. Un remboursement depuis un autre compte (coffre, banque) ne touche pas la session, comme les remboursements
  d'avoir.
- Vente verrouillée (`FOR UPDATE`) au début de la transaction ; statut, quantités déjà retournées et reste dû relus
  sous ce verrou.
- `Restocked` vrai seulement si un article est réellement revenu en stock.

**Tests.** `SaleReturnCashTests` : vente 300 + retour espèces depuis la session → mouvement −300, clôture au fond de
caisse sans écart, trésorerie inchangée ; refus sans compte (`pos.refund_account_required`) et depuis une session close
(`pos.cash_session_closed`) ; deux retours simultanés de 6 sur 10 vendus → un seul accepté ; retour sans remise en stock.
Non-régression : caisse, paiements, comptes clients, guide fonctionnel, recette, achats (88 tests).

## Étape 3 — Date de vente fixée par le serveur (A-003)

**Défaut.** La commande d'encaissement liait `SoldAt` depuis le corps HTTP de `/pos/checkout` et `/sales`. Un
caissier pouvait antidater une vente ; le contrôle de session acceptait une session **close** quand la date fournie
précédait sa fermeture (règle prévue pour le rejeu hors ligne). Constaté lors de l'audit : vente acceptée dans une
session déjà comptée et clôturée, mouvement de caisse ajouté après le comptage.

**Correction.**
- `/pos/checkout` et `/sales` ignorent la date fournie : la vente est datée par le serveur. L'interface de caisse
  rejoue ses ventes hors ligne par la synchronisation (`/sync`), seul canal qui garde la date de l'appareil.
- Date d'une vente rejouée : jamais plus de 5 minutes dans le futur (`pos.sold_at_future`), jamais avant l'ouverture
  de sa session (`pos.sold_at_outside_session`).

**Tests.** `SaleDateControlTests` : date future refusée ; vente datée avant l'ouverture de sa session refusée ; après
clôture, une vente datée par le serveur est refusée (`pos.session_closed`). Non-régression : caisse, synchronisation
hors ligne, caisse hors ligne, commandes, restaurant, terrain, ventes, guide, date métier (97 tests).

**Conséquence de l'étape 2 à connaître.** Une demande de retour d'un caissier approuvée par un manager après la clôture
de la session du caissier ne peut plus être remboursée depuis ce tiroir clos (`pos.cash_session_closed`) : le manager
rembourse depuis une session ouverte.
