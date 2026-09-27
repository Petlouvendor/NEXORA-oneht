# NEXORA — PHASE 24 — STOCK, ENTREPÔTS, LOTS & INVENTAIRES

**Moteur de gestion des stocks multi-secteurs NEXORA V1.0**
**Version 1.0**

---

## 1. Objectif

Construire un moteur de stock capable de fonctionner pour une petite boutique, un supermarché multi-caisses, une pharmacie avec lots et expirations, un restaurant avec ingrédients, un grossiste avec plusieurs entrepôts ou une entreprise de production. Le stock doit rester relié aux achats, ventes, retours, inventaires, finance et comptabilité.

## 2. Principe

```
ENTRÉES
Achats • Retours clients • Transferts entrants • Production
          ↓
      MOUVEMENTS
          ↓
       STOCK
          ↓
SORTIES
Ventes • Retours fournisseurs • Transferts • Consommation • Ajustements
```

- Le mouvement est la trace fondamentale.
- Toute variation importante doit avoir une source.
- Le stock physique et le stock système doivent être réconciliables.
- Les corrections sont des mouvements, pas des modifications silencieuses de solde.

## 3. Structure

```
COMPANY
 └── ESTABLISHMENT
      ├── WAREHOUSE
      │    └── LOCATION
      └── CASH / POS
PRODUCT
 └── INVENTORY ITEM
      └── LOT
           └── STOCK MOVEMENTS
```

## 4. Entrepôts

Nom · Code · Type · Établissement · Adresse/localisation · Responsable · Statut · Capacité optionnelle · Paramètres.

## 5. Types d'emplacements

Magasin · Entrepôt · Réserve · Chambre froide · Pharmacie/stock sécurisé · Cuisine · Production · Transit · Retour · Quarantaine · Emplacement virtuel.

## 6. Emplacements

Code emplacement · Zone · Rayon · Étagère · Bin · Capacité · Statut · Produit préféré optionnel.

## 7. Stock par établissement

Une entreprise peut vendre dans plusieurs établissements. Le stock doit être séparé par emplacement afin d'éviter les confusions entre magasins.

```
PRODUCT + COMPANY + WAREHOUSE + LOCATION → BALANCE
```

## 8. Stock disponible

Stock physique · Réservé · Disponible · En transit · En quarantaine · Endommagé · Expiré · En commande · Stock de sécurité.

```
AVAILABLE = ON_HAND − RESERVED − BLOCKED
```

## 9. Réservations

Commande client · Commande interne · Production · Transfert · Autre réservation · Expiration de réservation · Libération automatique selon règle.

## 10. Mouvements

Réception achat · Vente · Retour client · Retour fournisseur · Transfert · Ajustement · Inventaire · Consommation recette · Production · Perte · Casse · Expiration · Correction contrôlée.

## 11. Mouvement de stock

```
STOCK MOVEMENT
├── source
├── destination
├── product
├── lot
├── quantity
├── unit
├── unit_cost
├── occurred_at
└── reference
```

Type · Quantité positive/négative selon convention · Unité · Coût lorsque applicable · Source document · Utilisateur · Date · Motif.

## 12. Lots

Numéro lot · Produit · Entreprise · Fournisseur · Date réception · Date fabrication optionnelle · Expiration · Quantité · Statut · Document source.

## 13. Expiration

Date expiration · Jours avant alerte · Statut normal/proche/expiré · Blocage vente optionnel · Blocage automatique selon configuration · Rapport expirations · Notifications.

## 14. FEFO

Pour les produits soumis à expiration, NEXORA peut proposer FEFO : First Expired, First Out.

```
VENTE → LOTS DISPONIBLES → TRI EXPIRATION → LOT LE PLUS PROCHE
```

- Activation par produit/catégorie/entreprise.
- Override contrôlé.
- Audit si un lot différent est choisi.
- Compatibilité avec réservation.

## 15. Pharmacie

Lots obligatoires lorsque requis · Expiration · Quarantaine · Blocage lot · Traçabilité réception → vente · Retrait/rappel produit · Rapport lots clients lorsque les données permettent la traçabilité.

Les règles réglementaires spécifiques au pays doivent être configurables et validées localement.

## 16. Restaurant

Stock ingrédients · Unités cuisine · Recettes · Consommation à la vente · Pertes · Déchets · Production préparée · Inventaire périodique · Coût matière.

## 17. Supermarché

Code-barres · Produits pondérés · PLU · Stock par magasin · Transferts · Casse · Péremption · Inventaires rapides · Promotions liées au stock.

## 18. Grossiste

Cartons · Palettes · Unités multiples · Lots · Entrepôts · Réservations · Préparation commande · Expédition · Stock en transit.

## 19. Production

Matières premières · Ordres de fabrication · Consommation · Rendement · Produits finis · Pertes · Sous-produits optionnels · Coût de production.

## 20. Inventaire

```
PRÉPARATION → COMPTAGE → ÉCART → VALIDATION → AJUSTEMENT → AUDIT
```

Inventaire complet · partiel · cyclique · Comptage aveugle · Double comptage · Validation par responsable · Blocage mouvements optionnel.

## 21. Comptage

Inventaire · Emplacement · Produit · Lot · Quantité système · Quantité comptée · Écart · Utilisateur · Date · Deuxième comptage optionnel.

## 22. Ajustements

Motif obligatoire · Permission · Quantité · Coût · Impact finance/comptabilité selon règles · Audit · Approbation selon seuil.

## 23. Inventaire tournant

Planifier fréquences · Catégories prioritaires · Produits à forte valeur · Produits à forte rotation · Expiration · Écarts historiques · Tâches automatiques.

## 24. Transferts

```
ENTREPÔT A → TRANSFERT → EN TRANSIT → ENTREPÔT B → RÉCEPTION
```

Création · Validation · Préparation · Expédition · Transit · Réception · Écart · Annulation selon état · Audit.

## 25. Transfert inter-établissements

Source entreprise/établissement · Destination · Produits · Lots · Quantités · Document · Statut · Réception · Pertes/écarts.

## 26. Réservations et commandes

Réserver stock · Dé-réserver · Stock disponible · Stock promis · Priorité · Échéance · Allocation par lot lorsque nécessaire.

## 27. Valorisation

Coût moyen · FIFO lorsque retenu · Coût standard · Coût par lot · Méthode configurable par entreprise selon besoins comptables · Méthode choisie documentée · Changement contrôlé et historisé.

La méthode de valorisation doit être compatible avec les règles comptables adoptées par l'entreprise et son contexte réglementaire.

## 28. Coût moyen

```
NOUVEAU COÛT MOYEN = (VALEUR EXISTANTE + VALEUR ENTRANTE) / QUANTITÉ TOTALE
```

Calcul exact · Cas retours · Ajustements · Multi-devise · Tests de précision.

## 29. Coût et comptabilité

```
RÉCEPTION → STOCK → COÛT → COMPTABILISATION SELON CONFIGURATION
VENTE → SORTIE STOCK → COÛT DES VENTES → COMPTABILITÉ
```

## 30. Seuils

Minimum · Maximum · Point de commande · Stock sécurité · Alerte rupture · Alerte surstock · Alerte faible rotation · Paramètre par établissement.

## 31. Réapprovisionnement

Seuil déclenchement · Quantité cible · Fournisseur préféré · MOQ · Délai · Suggestions · Commande automatique selon workflow · Approbation.

## 32. Stock négatif

Interdit par défaut · Autorisation configurable · Permission spéciale · Alerte immédiate · Audit · Régularisation · Impact comptable contrôlé.

## 33. Pertes et casse

Type perte · Motif · Produit · Lot · Quantité · Valeur · Responsable · Approbation selon seuil · Audit.

## 34. Retours clients

```
RETOUR → CONTRÔLE → LOT/PRODUIT → STOCK / QUARANTAINE / DÉTRUIT
```

Remise en stock · Quarantaine · Non revendable · Remboursement · Audit.

## 35. Retours fournisseurs

```
RETOUR FOURNISSEUR → SORTIE STOCK → CRÉDIT FOURNISSEUR
```

## 36. Stock en transit

Transfert expédié · Quantité en transit · Destination prévue · Date départ · Réception · Retard · Écart.

## 37. Scanner

Scan entrée · sortie · inventaire · transfert · lot · produit · Compatible code-barres/QR selon matériel.

## 38. Recherche stock

Produit · SKU · Code-barres · Lot · Entrepôt · Emplacement · Expiration · Statut · Fournisseur.

## 39. Dashboard stock

Stock total · Ruptures · Surstock · Produits proches expiration · Valeur stock · Rotation · Mouvements · Inventaires en cours · Transferts · Écarts.

## 40. Rapports

Stock par produit · par entrepôt · par établissement · Lots · Expiration · Mouvements · Inventaires · Écarts · Valorisation · Rotation · Casse · Pertes · Stock dormant.

## 41. API principales

```
GET  /api/v1/warehouses
POST /api/v1/warehouses
GET  /api/v1/inventory
GET  /api/v1/stock-movements
POST /api/v1/stock-adjustments
POST /api/v1/transfers
POST /api/v1/transfers/{id}/ship
POST /api/v1/transfers/{id}/receive
POST /api/v1/inventory-counts
POST /api/v1/inventory-counts/{id}/validate
GET  /api/v1/lots
GET  /api/v1/stock-alerts
```

## 42. Permissions

Lire stock · Créer réception · Ajuster stock · Créer transfert · Expédier · Réceptionner · Valider inventaire · Modifier paramètres · Voir coûts · Voir valorisation · Approuver ajustement.

## 43. Audit

Ajustement · Inventaire · Transfert · Modification seuil · Modification valorisation · Blocage lot · Déblocage lot · Suppression/archivage · Override FEFO · Stock négatif autorisé.

## 44. Concurrence

Vente simultanée · Réservation simultanée · Réception et vente concurrentes · Deux caissiers sur même stock · Inventaire pendant activité · Transferts simultanés · Transactions atomiques · Tests de course.

## 45. Offline

- Vente offline réserve/décrémente selon stratégie locale.
- Synchronisation des mouvements.
- Détection conflit.
- Réconciliation serveur.
- Ne jamais accepter deux effets financiers/stock identiques.
- État clairement affiché au POS.

## 46. Performance

Index produit/warehouse/location · Index lots expiration · Recherche code-barres · Pagination mouvements · Agrégations pré-calculées seulement si nécessaire · Traitements lourds asynchrones · Inventaire massif asynchrone lorsque nécessaire.

## 47. Tests

Réception · Vente · Retour · Transfert · Inventaire · Ajustement · Lot · Expiration · FEFO · Stock négatif · Réservation · Concurrence · Offline · Valorisation · Pharmacie · Restaurant · Supermarché · Grossiste · Production.

## 48. Scénario de validation complet

```
ACHAT 100 UNITÉS → RÉCEPTION 100 → STOCK +100
      ↓
VENTE 10 → STOCK 90
      ↓
RETOUR 2 → STOCK 92
      ↓
TRANSFERT 20 → SOURCE 72 / TRANSIT 20
      ↓
RÉCEPTION DESTINATION → SOURCE 72 / DESTINATION +20
```

## 49. Critères de sortie

Entrepôts opérationnels · Emplacements · Stock par entreprise · Mouvements · Lots · Expiration · FEFO · Inventaires · Ajustements · Transferts · Réservations · Seuils · Réapprovisionnement · Pertes/casse · Retours · Valorisation · Comptabilité stock · Dashboard · Rapports · API · Permissions · Audit · Offline · Tests.

## 50. Étape suivante

Passer à la **Phase 25 — Ventes, POS, commandes, retours, paiements et expérience caisse**. Elle construira le moteur de vente complet en s'appuyant sur le catalogue et le stock, puis alimentera directement la finance, les créances et la comptabilité.

---

**NEXORA V1.0 — PHASE 24 VALIDÉE → PHASE 25 : VENTES & POS**
