# Finalisation produit — Phase 5 : Caisse, encaissement en plusieurs moyens

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 5.
> Branche `feature/pos-payments`. Date : 2026-09-28.

## 1. Travail effectué

Avant cette phase, la caisse n'encaissait qu'**un seul montant**, toujours sur le compte du tiroir et **sans moyen
de paiement** : une vente payée par carte ou MonCash était enregistrée comme des espèces.

| Écran caisse | Après |
|---|---|
| Encaissement | une ligne par moyen : espèces, carte, mobile money (MonCash), virement, chèque… selon les moyens actifs de l'entreprise ; « + Carte », « + MonCash » ajoutent une ligne préremplie du reste dû |
| Espèces | montants rapides (Exact, billets supérieurs) ; montant vide = paiement exact en espèces |
| Carte / mobile money | référence obligatoire quand le moyen l'exige (n° d'autorisation, de transaction) ; compte d'encaissement proposé (choix si plusieurs) |
| Contrôles à l'écran | total encaissé, reste à encaisser, rendu monnaie ; alerte et blocage si la carte dépasse le dû ou si une référence manque |
| Confirmation | détail de chaque encaissement (moyen, référence, montant), total reçu, rendu |

## 2. Défauts corrigés

1. **Carte et MonCash comptés comme des espèces** : un seul encaissement, sans moyen, sur le compte du tiroir —
   la trésorerie mélangeait carte et liquide, et l'attendu du tiroir à la clôture était faux.
2. **Rendu monnaie déduit du dernier encaissement, quel qu'il soit** : si le dernier était une carte, le rendu était
   retiré de la carte (impossible dans la réalité, montant de carte faux en comptabilité).
3. **Aucun contrôle des encaissements** : compte inconnu, inactif, d'une autre devise (une carte HTG pouvait être
   encaissée sur un compte en USD), moyen inactif, référence exigée absente, montant négatif — tout était accepté.
4. **Espèces encaissées hors de la caisse de la session** possibles (le tiroir ne correspondait plus au comptage).
5. Un test existant encaissait justement une carte HTG sur un compte USD : le test était faux, il est corrigé.

## 3. Base de données

Aucune migration : `payment_method` (type, référence exigée, devise) et `financial_account` (type) existaient.
Le compte d'encaissement par défaut d'un moyen peut être fixé par `payment_method.settings.settlementAccountId`.

## 4. APIs

Nouvelle : `GET /pos/tender-options?currency=` — moyens actifs et comptes compatibles (espèces → caisse ;
carte / virement / chèque → banque ; mobile money → compte mobile money), sans aucun solde.
Modifiée : `POST /pos/checkout` (et la livraison des commandes, qui passe par le même moteur) contrôle chaque
encaissement.

## 5. Règles garanties par le serveur

- Chaque encaissement : montant ≥ 0 (0 ignoré), compte actif de l'entreprise dans la devise de la vente, moyen
  actif, référence si exigée.
- Espèces ↔ compte caisse (celui de la session ouverte) ; carte, virement, mobile money… jamais dans une caisse.
- Seules les espèces rendent la monnaie ; les autres moyens ne dépassent jamais le reste dû.
- Seules les espèces nettes (après rendu) entrent dans l'attendu du tiroir.
- Hors ligne : sans liste des moyens, la caisse revient aux espèces seules (la vente mise en file reste contrôlée
  par le serveur à la synchronisation).

## 6. Tests

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend (dont 3 nouveaux `PosTenderTests`) | 445 / 445 |
| API backend | 186 / 186 |
| Frontend Vitest (dont 5 nouveaux), en fuseau `America/Port-au-Prince` | 218 / 218 |
| Scénario API bout en bout (clone `nexora_qa`, caissier) | 9 / 9 |
| Parcours navigateur (carte + espèces, rendu, blocages) | réussi, 0 erreur console |

## 7. Dette technique restante

- Pas encore de terminal de paiement ni d'API MonCash réelle : la carte et le mobile money sont saisis (montant +
  référence), pas autorisés en ligne (phase 15, connecteurs).
- Écran d'administration des moyens de paiement (création, compte par défaut) : à faire en phase 11 (administration).
- Paiement en devise étrangère avec change à la caisse : non géré (un encaissement doit être dans la devise de la vente).
- Clôture de caisse : l'attendu est désormais juste pour les espèces ; un récapitulatif par moyen (carte, MonCash) au
  moment de la clôture reste à afficher.
