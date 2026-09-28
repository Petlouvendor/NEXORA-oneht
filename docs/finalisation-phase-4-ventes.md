# Finalisation produit — Phase 4 : Ventes, devis, commandes clients, facturation

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 4.
> Branche `feature/sales`. Date : 2026-09-28.

## 1. Travail effectué

Avant cette phase, les écrans Commandes et Documents étaient en lecture : **aucune création de devis ni de
commande**, aucun acompte, une livraison uniquement « à crédit » sans encaissement, aucune émission de facture,
et trois actions passant par `window.prompt`. Le moteur serveur existait (phases 25 et 30) sans écran.

| Écran | Après |
|---|---|
| Ventes › Devis | création et modification du brouillon (client, validité, conditions, articles au prix catalogue ou dérogé, remise, taxe), envoyé, accepté, **refusé avec motif**, **annulé avec motif**, conversion en commande (type, **emplacement**, comptant / crédit, échéance), PDF |
| Ventes › Commandes clients | création et **modification du brouillon**, confirmation, réservation du stock, **acompte** (compte d'encaissement), **livraison totale ou partielle** avec plusieurs moyens de paiement ou à crédit, annulation motivée, PDF du bon de commande, proforma / facture d'acompte / bon de livraison, ventes générées et leur reste dû |
| Fiche vente | **Émettre la facture** (ouverte en PDF), documents de la vente |
| Documents | filtres type / statut, fiche du document, **vrai PDF A4 ou ticket**, envoi (canal, destinataire contrôlé), annulation motivée, historique des envois, vérification par code |

## 2. Défauts corrigés

1. **Acompte payé deux fois** : l'acompte était encaissé en compte d'attente puis ignoré à la livraison ; le client
   payait la totalité une seconde fois et l'acompte restait indéfiniment en attente.
2. **Acompte invisible au compte client** (aucune écriture au journal client).
3. **Livraison « au comptant » incomplète acceptée** : le reste non payé devenait silencieusement une créance.
4. **Réservation jamais libérée** à l'annulation d'une commande réservée puis partiellement livrée.
5. **Commande issue d'un devis inexploitable** : créée sans emplacement de stock, elle ne pouvait ni être réservée ni
   livrée, et aucune modification n'était possible.
6. **« PDF » qui n'en était pas un** : le lien renvoyait une page HTML (`text/html`, fichier `.html`), et sans jeton
   d'authentification il ne pouvait pas s'ouvrir depuis l'application.
7. **Lignes vides dans les documents émis** : le contenu figé enregistrait `Description` alors que le rendu lisait
   `description`.
8. **Facture émise sur une vente annulée** possible.
9. **Devise inconnue → erreur 500** (devis et commandes) ; validité de devis dans le passé acceptée ; devis envoyé et
   périmé encore acceptable tant que le planificateur n'était pas passé.
10. Pour un client bloqué au crédit, une livraison au comptant incomplète affichait « client bloqué pour le crédit »
    au lieu de « il reste X à encaisser » (constaté pendant le scénario de bout en bout).
11. Refus de devis sans motif accepté ; lignes en double, quantités nulles acceptées sur commandes et devis.

## 3. Base de données — V083

- `customer_credit_memo` : origine `order_deposit` (avoir rattaché à une commande via `sales_order_id`) ; journal
  client : type `order_deposit`, libellé « Acompte sur commande ».
- Reprise : acompte existant non affecté → avoir (aucun sur `nexora` ni `nexora_recette`).
- Audit des lignes de commande client.
Appliquée sur `nexora` et `nexora_recette` ; embarquée dans l'API.

## 4. APIs

Nouvelles : `PUT /orders/{id}`, `GET /orders/payment-options`, `PUT /quotes/{id}`, `POST /quotes/{id}/cancel`,
`GET /print/quote|sales-order|document/{id}`, `GET /documents?sourceType=&sourceId=`.
Modifiées : `GET /documents/{id}/pdf` renvoie un vrai PDF ; `POST /quotes/{id}/convert` accepte l'emplacement ;
`GET /orders/{id}` renvoie client, emplacement, acompte disponible, ventes générées, motif d'annulation.

## 5. Règles garanties par le serveur

- Prix, promotions et taxes figés par le serveur (devis puis commande puis vente) ; le client n'envoie que des
  quantités et d'éventuelles dérogations contrôlées.
- Acompte = avoir du client : imputé en priorité sur la vente de sa commande, jamais payé deux fois ; en cas
  d'annulation, il reste utilisable ou remboursable (circuit de remboursement existant, 4 yeux).
- Livraison au comptant : le reste doit être encaissé en totalité, sinon refus explicite et rien n'est écrit.
- Commande et devis modifiables seulement en brouillon ; une vente annulée ne se facture pas ; une vente n'a qu'une
  facture valide ; un document émis est figé, annulable seulement avec motif.
- E-mail / SMS sans connecteur : envoi enregistré « en file », jamais présenté comme envoyé.

## 6. Tests

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend (dont 7 nouveaux `SalesFlowTests`) | 442 / 442 |
| API backend | 186 / 186 |
| Frontend Vitest (dont 6 nouveaux), en fuseau `America/Port-au-Prince` | 213 / 213 |
| Scénario API bout en bout (clone `nexora_qa`) | 37 / 37 |
| Parcours navigateur (devis → commande → acompte → livraison → facture → envoi) | réussi, 0 erreur console |

## 7. Dette technique restante

- Le module POS encaisse toujours une seule vente par panier ; le paiement multiple de la caisse est la phase 5.
- Facture de solde (`final_invoice`) : émissible mais sans calcul automatique « total − acomptes facturés ».
- Envoi réel d'e-mail / SMS : dépend des connecteurs (phase 15) ; aujourd'hui journalisé en file.
- La modification d'un brouillon de commande recalcule les prix au tarif du jour (pas de conservation d'un prix
  dérogé sans le ressaisir).
- Numérotation des documents par type : configurable côté serveur, pas encore d'écran.
