# NEXORA — PHASE 25 — VENTES, POS, COMMANDES & PAIEMENTS

**Moteur de vente et expérience caisse NEXORA V1.0**
**Version 1.0**

---

## 1. Objectif

Construire le moteur de vente universel de NEXORA : caisse tactile, vente comptant ou à crédit, commandes, paiements multiples, retours, remboursements, promotions, clients, tickets, clôture de caisse et intégration automatique avec stock, finance et comptabilité.

## 2. Principe

```
CATALOGUE → POS / COMMANDE → PRIX + TAXES + PROMOTIONS → VALIDATION
         → PAIEMENT OU CRÉDIT → STOCK + FINANCE + COMPTABILITÉ + AUDIT
```

- Le backend est l'autorité sur les règles métier.
- Une vente validée ne doit pas être modifiée silencieusement.
- Chaque opération financière est traçable.
- Le POS doit rester utilisable avec faible latence.
- Le mode offline doit être explicitement géré.

## 3. Types de ventes

Vente immédiate · à crédit · commande à retirer · commande à livrer · comptoir · sur réservation · en gros · de services · de produits composés · offline.

## 4. Cycle de vente

```
BROUILLON → PANIER → VALIDATION → PAIEMENT / CRÉDIT → CONFIRMÉE
         → RÉGLÉE / PARTIELLEMENT RÉGLÉE → CLÔTURÉE
```

- Les états autorisés et transitions sont définis côté backend.
- Une annulation n'efface pas l'historique.

## 5. POS

Recherche produit · Scanner · Catégories · Favoris · Panier · Client · Prix · Quantité · Remises · Taxes · Promotions · Paiement · Crédit · Ticket · Suspendre · Reprendre · Retour.

## 6. Écran POS

```
┌─────────────────────────────────────────────┐
│ ENTREPRISE • CAISSE • UTILISATEUR • ONLINE   │
├──────────────────────┬──────────────────────┤
│ RECHERCHE / SCAN     │ PANIER               │
│ CATÉGORIES           │ ARTICLES             │
│ FAVORIS              │ REMISES              │
│ PRODUITS             │ TAXES                │
│                      │ TOTAL                │
├──────────────────────┴──────────────────────┤
│ SUSPENDRE | CLIENT | RETOUR | PAIEMENT       │
└─────────────────────────────────────────────┘
```

## 7. Recherche

Code-barres exact · SKU · Nom · Alias · PLU · Référence · Recherche tolérante · Recherche client · Résultats rapides · Historique récent.

## 8. Panier

Ajouter · Modifier quantité · Supprimer · Remise ligne · Remise panier · Taxe · Lot · Note · Client · Prix autorisé · Stock disponible · Suspension.

## 9. Snapshots transactionnels

La vente doit conserver les informations nécessaires à sa reconstitution : description, prix, taxe, remise, unité, devise, taux et autres données pertinentes au moment de la vente.

## 10. Prix et promotions

```
BASE PRICE → PRICE LIST → QUANTITY RULE → PROMOTION → DISCOUNT → TAX → TOTAL
```

Ordre des règles explicite · Résultat affichable · Cumul contrôlé · Priorité · Période de validité · Audit pour remise manuelle.

## 11. Remises

Pourcentage · Montant · Ligne · Panier · Promotion automatique · Remise manuelle · Limite par rôle · Limite par montant · Motif obligatoire au-delà d'un seuil · Audit.

## 12. Taxes

HT/TTC selon configuration · Taxe ligne · Taxe panier · Exonération · Arrondi · Compte taxe · Snapshot du taux appliqué.

## 13. Paiements

Espèces · Carte · Mobile Money · Virement selon workflow · QR/lien lorsque intégration disponible · Paiement multiple · partiel · en attente · Crédit · Remboursement.

## 14. Paiement multiple

```
TOTAL 10 000
├── CASH   4 000
├── CARD   3 000
└── MOBILE 3 000
        = 10 000
```

Chaque allocation est enregistrée · Somme des paiements contrôlée · Rendu monnaie calculé sur espèces selon règles · Idempotence.

## 15. Espèces

Montant reçu · Rendu monnaie · Caisse · Session · Devise · Écart éventuel · Audit.

## 16. Paiement externe

Création transaction · Statut pending · Confirmation fournisseur · Succès · Échec · Timeout · Webhook · Remboursement · Idempotency key.

Une réponse positive du frontend ne suffit jamais à confirmer un paiement externe.

## 17. Vente à crédit

```
CLIENT → VÉRIFICATION CRÉDIT → LIMITE / CONDITIONS → VENTE → CRÉANCE → ÉCHÉANCE → PAIEMENTS
```

Client obligatoire · Solde actuel · Limite · Crédit disponible · Échéance · Paiement partiel · Blocage dépassement · Autorisation exceptionnelle · Audit.

## 18. Commande

Création · Client · Lignes · Réservation stock · Acompte · Statut · Préparation · Livraison/retrait · Paiement · Facturation.

## 19. Commande à crédit

Acompte éventuel · Solde · Échéance · Limite client · Livraison selon politique · Créance à la facturation/validation selon workflow.

## 20. Livraison

Adresse · Créneau · Responsable · Statut · Preuve livraison · Paiement à la livraison selon configuration · Impact stock · Document.

## 21. Vente de service

Service · Quantité/durée · Prix · Taxe · Employé/prestataire optionnel · Commission optionnelle · Aucun mouvement stock · Finance/comptabilité.

## 22. Restaurant

Table · Commande · Articles · Options · Suppléments · Cours · Notes cuisine · Statut préparation · Addition · Partage addition · Pourboire selon configuration.

## 23. Supermarché

Scanner · Produit pesé · PLU · Prix/kg · Étiquette · Promotions · Multi-caisses · Retour · Clôture.

## 24. Pharmacie

Scan · Lot · Expiration · FEFO · Client · Traçabilité selon données disponibles · Blocage produit/lot selon configuration · Retour contrôlé.

## 25. Grossiste

Tarifs quantité · Cartons · Unités · Commande · Réservation · Livraison · Crédit · Facturation.

## 26. Retours

```
VENTE SOURCE → SÉLECTION LIGNES → MOTIF → CONTRÔLE → STOCK / QUARANTAINE → REMBOURSEMENT / AVOIR → COMPTABILITÉ
```

Retour total/partiel · Quantité maximale contrôlée · Retour stockable ou non · Lot · Motif · Autorisation · Audit.

## 27. Remboursements

Espèces · Retour moyen d'origine lorsque possible · Autre moyen autorisé · Remboursement partiel · Statut externe · Référence · Audit.

## 28. Avoirs

Document source · Montant · Solde · Utilisable ultérieurement · Expiration selon politique · Utilisation partielle · Comptabilité.

## 29. Annulation

Annulation avant validation selon état · Annulation après validation remplacée par retour/contrepassation appropriée · Motif · Permission · Audit.

## 30. Ventes suspendues

Panier · Utilisateur · Caisse · Client · Horodatage · Restauration · Expiration · Audit.

## 31. Tickets et factures

Ticket POS · Facture · Avoir · Bon de commande · Bon de livraison · Reçu paiement · QR code optionnel · Numéro unique · Template.

## 32. Impression

Imprimante par caisse · Format ticket · Aperçu · Réimpression selon permission · Détection indisponibilité · Queue d'impression si nécessaire.

## 33. Clôture caisse

```
VENTES + PAIEMENTS + ENTRÉES/SORTIES = ATTENDU → COMPTAGE → ÉCART → VALIDATION
```

Ouverture · Fond de caisse · Mouvements · Ventes · Paiements · Comptage · Écart · Motif · Clôture · Audit.

## 34. Sessions de caisse

Une session active par caisse selon politique · Utilisateur responsable · Horodatage · Ouverture · Fermeture · Statut · Écart.

## 35. Gestion espèces

Entrée caisse · Sortie caisse · Dépense caisse · Fond · Retrait sécurisé · Motif · Approbation selon seuil · Audit.

## 36. Caisse multi-utilisateur

Affectation caissier · Connexion personnelle · Changement opérateur · Traçabilité par utilisateur · Restrictions par caisse · Clôture responsable.

## 37. Offline POS

```
VENTE LOCALE → LOCAL OUTBOX → PENDING → RÉSEAU → SYNC → SERVER → CONFIRMED / CONFLICT / FAILED
```

Identifiant local unique · Idempotence serveur · État visible · Retry · Conflit explicite · Aucun double mouvement stock/paiement.

## 38. Stock lors de la vente

Vérifier disponibilité · Réserver si commande · Décrémenter à la validation selon politique · Associer lot · FEFO · Gérer retour · Audit.

## 39. Finance lors de la vente

```
VENTE COMPTANT → CAISSE/BANQUE/MOBILE MONEY
VENTE CRÉDIT   → CRÉANCE CLIENT
RETOUR         → SORTIE FINANCIÈRE / AVOIR
```

## 40. Comptabilité lors de la vente

```
VENTE
├── PRODUIT / SERVICE
├── TAXE
├── PAIEMENT ou CRÉANCE
└── COÛT DES VENTES / STOCK selon configuration
```

Les comptes exacts proviennent du mapping comptable de l'entreprise.

## 41. Fraude et contrôles

Remises anormales · Annulations fréquentes · Retours inhabituels · Remboursements sans vente source · Ouvertures/fermetures inhabituelles · Écarts caisse · Ventes offline atypiques · Crédits hors politique · Actions administratives auditables.

## 42. Permissions POS

Vendre · Remise · Modifier prix · Vendre à crédit · Retour · Remboursement · Annuler · Suspendre · Réimprimer · Ouvrir caisse · Fermer caisse · Entrée/sortie caisse · Voir rapports caisse.

## 43. API principales

```
POST /api/v1/sales
GET  /api/v1/sales/{id}
POST /api/v1/sales/{id}/payments
POST /api/v1/sales/{id}/returns
POST /api/v1/sales/{id}/refunds
POST /api/v1/sales/{id}/cancel
POST /api/v1/orders
GET  /api/v1/orders/{id}
POST /api/v1/orders/{id}/reserve
POST /api/v1/orders/{id}/complete
POST /api/v1/cash-sessions
POST /api/v1/cash-sessions/{id}/close
POST /api/v1/cash-movements
```

## 44. Événements métier

```
SaleCreated · SaleConfirmed · PaymentCreated · PaymentConfirmed · PaymentFailed
CreditCreated · StockDeducted · SaleReturned · RefundCreated · CashSessionClosed
```

Événements fiables via outbox · Consommateurs idempotents · Audit.

## 45. Tests fonctionnels

Vente simple · multi-lignes · Scanner · Produit inconnu · Rupture · Prix promotionnel · Remise autorisée · Remise interdite · Taxe · Paiement espèces · multiple · externe · Crédit · partiel · Retour · Remboursement · Avoir · Commande · Livraison · Clôture caisse.

## 46. Tests financiers

Total ticket = somme lignes + taxes − remises · Paiements = montant dû · Créance correcte · Remboursement correct · Caisse correcte · Écriture comptable équilibrée · Stock correct · Aucun double paiement · Aucun double mouvement stock.

## 47. Tests concurrence

Deux ventes du dernier article · Deux paiements identiques · Deux remboursements · Deux caissiers · Clôture pendant tentative de vente · Synchronisation offline simultanée.

## 48. Tests sécurité

Caissier hors scope · Modification prix interdite · Crédit sans permission · Retour sans permission · Remboursement sans permission · Accès vente autre entreprise · ID manipulé · Webhook doublé · Session expirée.

## 49. Tests performance

Recherche < objectif UX · Ajout panier rapide · Validation vente stable sous charge · Recherche simultanée multi-caisses · Clôtures simultanées · Synchronisation offline massive.

## 50. Critères de sortie

POS fonctionnel · Ventes comptant · Ventes crédit · Commandes · Paiements multiples · Paiements externes abstraits · Retours · Remboursements · Avoirs · Caisse · Clôture · Stock intégré · Finance intégrée · Comptabilité intégrée · Offline · Audit · Anti-fraude · Permissions · API · Tests.

## 51. Étape suivante

Passer à la **Phase 26 — Achats, fournisseurs, réceptions, factures, achats à crédit et dettes fournisseurs**. Elle construira le cycle d'approvisionnement complet et son intégration avec stock, finance et comptabilité.

---

**NEXORA V1.0 — PHASE 25 VALIDÉE → PHASE 26 : ACHATS & FOURNISSEURS**
