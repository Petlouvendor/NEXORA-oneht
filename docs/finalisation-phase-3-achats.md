# Finalisation produit — Phase 3 : Achats, réceptions, factures fournisseur, retours

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 3.
> Branche `feature/purchases`. Date : 2026-09-28.

## 1. Travail effectué

Avant cette phase, la page **Achats** ne permettait **ni de créer une commande, ni de réceptionner, ni de saisir
une facture, ni d'enregistrer un retour** : elle affichait des listes et deux boutons (approuver, envoyer).
Le moteur existait côté serveur (phases 5 et 26) sans écran.

| Onglet | Après |
|---|---|
| Commandes | création en **brouillon** ou soumise, modification tant qu'elle n'est pas approuvée, recherche d'article dans le catalogue, **unité d'achat** (carton → unités de base), taxe par ligne, remise, prix d'achat connu du fournisseur proposé, estimation des totaux ; fiche commande : soumettre, approuver, envoyer, annuler, **solder** un reliquat, réceptionner, saisir la facture, imprimer, réceptions et factures liées |
| Réceptions | réception d'une commande (reste à recevoir prérempli, en cartons s'il tombe juste) ou **sans commande** ; quantité acceptée / **refusée avec motif** ; coût, lot, péremption ; bon de réception consultable et imprimable ; retour depuis le bon |
| Factures | saisie depuis une commande (quantités reçues au prix commandé) ou libre (articles, **lignes libres** transport / service) ; à crédit ou payée comptant (compte choisi) ; marchandises ou charges ; référence fournisseur anti-doublon ; fiche facture avec **rapprochement 3-way** et reste à payer |
| Dettes | inchangé (paiement, reçu imprimable) |
| Retours | retour depuis une réception ou libre ; motif obligatoire ; **imputation sur une dette** ou avoir à recevoir |
| Fournisseurs | création, **fiche** (achats 12 mois, dettes, retard, commandes en cours, derniers prix d'achat constatés), **modification de l'identité** (la fonction existait sans écran), **conditions commerciales** (conditions de paiement, limite de crédit, contact, notes) |

## 2. Défauts corrigés

1. **Taxe de commande jamais calculée** : la taxe choisie sur une ligne était ignorée (`tax_total` toujours 0).
2. **Facture orpheline** : une facture pouvait être enregistrée alors que sa comptabilisation échouait (hors transaction). Tout est désormais atomique.
3. **Facture rejouée = doublon** : un second envoi de la même opération créait une seconde facture. Le rejeu renvoie la facture d'origine.
4. **Échéance jamais calculée** : les conditions de paiement fournisseur (table existante depuis V005) n'étaient utilisées nulle part. Une facture sans échéance prend désormais date + délai des conditions.
5. **Retour dont la comptabilisation échoue mais le stock sort** : même correction (transaction unique).
6. **Retour « sur dette » incohérent** : le grand livre débitait le compte Fournisseurs mais la dette gardait son montant — la balance fournisseurs divergeait du grand livre et **la marchandise retournée pouvait être payée**. La dette porte maintenant un montant d'avoirs, le reste à payer en tient compte (base de données), et l'avoir ne peut dépasser le reste à payer.
7. Retour d'une réception **supérieur à la quantité reçue** accepté.
8. **Rejeu idempotent en PascalCase** (tous les points idempotents : paiements, réceptions, factures, encaissements) : la réponse rejouée renvoyait `InvoiceId` au lieu de `invoiceId` ; un client qui réessayait après une coupure réseau lisait un identifiant indéfini.
9. **Dates affichées la veille** (toute l'application) : une date seule (`2026-09-28`) était lue comme minuit UTC, donc affichée le 27 en Haïti (UTC−4) — échéances, dates de facture, etc. Et « aujourd'hui » était calculé en UTC dans 11 écrans (le lendemain après 20 h). Corrigé dans le formateur partagé.
10. Modification d'une commande dans une même unité de travail après une transition : statut périmé (même famille que le défaut 8 de la phase 2) — l'entité est rechargée.
11. Désignation de facture répétant le SKU.

## 3. Base de données

**V081** — unité et quantité d'achat sur les lignes de commande et de réception (la quantité en unité de base reste
seule à piloter le stock), taxe par ligne de commande, quantité refusée + motif (contrainte : pas de refus sans
motif), fournisseur : conditions de paiement, contact, notes ; audit étendu aux lignes d'achat et aux conditions.

**V082** — `supplier_return.payable_id`, `supplier_payable.credited_amount`, reste à payer = original − payé −
avoirs (colonne générée), fonction unique `recompute_supplier_payable` (appelée par les paiements et les retours),
contrainte payé + avoirs ≤ montant. Appliquée sur `nexora` et `nexora_recette` ; embarquée dans l'API pour l'assistant d'installation.

## 4. APIs

Nouvelles : `PUT /purchasing/orders/{id}`, `POST /purchasing/orders/{id}/close`, `GET /purchasing/receipts`,
`GET /purchasing/receipts/{id}`, `GET /purchasing/invoices`, `GET /purchasing/invoices/{id}`, `GET /purchasing/returns`,
`GET /suppliers/{id}/overview`, `PUT /suppliers/{id}/terms`, `GET|POST /suppliers/payment-terms`.
Enrichie : `GET /purchasing/orders/{id}` (lignes en unité de base et d'achat, taxes, réceptions, factures).
Existantes désormais branchées : création de commande, soumission, annulation, réception, facture, retour, modification fournisseur.

## 5. Règles garanties par le serveur

- Totaux, taxes et conversions d'unité calculés par le serveur ; l'écran n'affiche qu'une estimation.
- Seule la quantité acceptée entre en stock ; une sur-réception est refusée ; un refus exige un motif.
- Commande modifiable seulement avant approbation ; ensuite seulement annulable ou soldable.
- Facture et retour : opération atomique (stock + comptabilité + dette) ou rien.
- Permissions : `purchasing.order / approve_order / receive / invoice / return / match / pay / view_payables`, `suppliers.create / update` (vérifié : caissier → 403).

## 6. Tests

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend (dont 6 nouveaux `PurchasingFlowTests`) | 435 / 435 |
| API backend (dont 1 nouveau test de casse du rejeu) | 186 / 186 (voir note) |
| Frontend Vitest (dont 11 nouveaux), exécutés aussi en fuseau `America/Port-au-Prince` | 207 / 207 |
| Scénario API bout en bout (clone `nexora_qa`) | 41 / 41 |
| Parcours navigateur (commande → approbation → réception avec refus → facture → fiche fournisseur) | réussi, 0 erreur console |

Note : lors d'une exécution de toute la solution (projets en parallèle), `SetupWizardTests.Fresh_server_is_installed…`
a échoué une fois (59 s, installation complète d'un serveur vierge sous charge) ; relancé seul puis avec tout le projet
API : vert (186 / 186). Non reproduit, consigné ici plutôt qu'écarté.

## 7. Dette technique restante

- Le retour ne reprend pas la taxe de la facture (montant de taxe retournée à 0 depuis l'écran).
- La limite de crédit fournisseur est enregistrée et affichée, mais ne bloque pas encore une commande.
- Pas de réception multi-lots d'un même article sur une ligne (une ligne = un lot).
- Escompte et pénalités de retard des conditions de paiement : enregistrés, non appliqués.
- `supplier_account` (V006) reste inutilisé (la balance fournisseurs vient des dettes).
