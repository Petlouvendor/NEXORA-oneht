# NEXORA — PHASE 5 — PRODUITS, STOCKS & ACHATS

**Spécification fonctionnelle du catalogue, des stocks et du cycle d'approvisionnement**
**Version 1.0**

> Document de référence. Implémentation : modules `Catalog` / `Inventory` / `Purchasing`
> du backend (voir [`phase-5-implementation.md`](phase-5-implementation.md)).

---

## 1. Objectif

Définir la gestion complète des produits, stocks, fournisseurs et achats de NEXORA. Le module doit fonctionner pour une petite boutique comme pour un supermarché, une pharmacie, un restaurant, un grossiste ou un groupe multi-sites.

## 2. Principes

- Un référentiel produit commun à tous les secteurs.
- Stocks suivis par emplacement.
- Chaque mouvement de stock est traçable.
- Les achats alimentent le stock et le moteur financier selon le type d'opération.
- Les achats à crédit créent des dettes fournisseurs.
- Les secteurs spécialisés ajoutent leurs données sans dupliquer le noyau.
- Les opérations sensibles sont auditables et soumises aux permissions.

## 3. Architecture fonctionnelle

```
CATALOGUE
   ↓
FOURNISSEUR → COMMANDE → RÉCEPTION → FACTURE
                              ↓
                           STOCK ↑
                              ↓
                    FINANCE / COMPTABILITÉ

VENTE → STOCK ↓

TRANSFERT → STOCK A ↓ + STOCK B ↑
INVENTAIRE → ÉCART → AJUSTEMENT → AUDIT
```

## 4. Catalogue produit

Création / modification / archivage · nom · description · SKU/référence · code-barres et identifiants externes · catégorie · sous-catégorie · marque · unité de vente · unité d'achat · image et infos complémentaires · statut actif/inactif · type (stockable, service, consommable, composé…).

## 5. Variantes et déclinaisons

Taille · couleur · format · dosage · conditionnement · autres attributs configurables. Les variantes sont gérées comme des références distinctes lorsque leur stock ou leur prix diffère.

## 6. Unités et conversions

Unité principale · unités d'achat/vente · conversion configurable · précision décimale · produits au poids/volume/longueur · contrôle des conversions.

```
1 CARTON = 12 UNITÉS
1 KG = 1000 G
```

Les conversions doivent être explicites afin d'éviter les erreurs de stock.

## 7. Catégories, marques et attributs

Catégories hiérarchiques · marques · attributs génériques · attributs spécialisés par secteur · archivage sans suppression de l'historique.

## 8. Prix et coûts

Prix d'achat courant · historique du prix d'achat · prix de vente · prix par quantité · prix par groupe client · prix promotionnel · devise · date de validité · coût de marge · règles d'arrondi.

Le prix d'une transaction validée reste historisé avec la vente ou l'achat ; une modification future ne réécrit pas l'historique.

## 9. Taxes produit

Taxe par défaut · taux configurable · exonération lorsque applicable · taxe par ligne ou transaction · historisation du taux appliqué.

## 10. Fournisseurs par produit

Fournisseurs multiples · fournisseur préféré · référence fournisseur · prix d'achat fournisseur · devise · délai moyen · quantité minimale · conditionnement · historique des prix.

## 11. Emplacements de stock

```
ENTREPRISE
 └── ÉTABLISSEMENT
      ├── MAGASIN
      ├── ENTREPÔT PRINCIPAL
      └── ENTREPÔT SECONDAIRE
```

Stock par entreprise / établissement / magasin / entrepôt · emplacement détaillé si activé · droits d'accès par emplacement.

## 12. État du stock

Quantité disponible · réservée · en commande · en transit · seuil min/max · point de commande · coût moyen ou méthode retenue · valeur du stock.

```
DISPONIBLE = EN STOCK − RÉSERVÉ
```

## 13. Mouvements de stock

Réception achat · vente · retour client · retour fournisseur · transfert sortie/entrée · inventaire · ajustement · perte · casse · expiration · correction contrôlée · production/consommation si activée.

Chaque mouvement conserve sa référence source, son emplacement, sa quantité, sa date et son utilisateur.

## 14. Transferts

Créer · choisir source et destination · réserver si nécessaire · expédier · réceptionner · gérer partiel · annuler avant réception selon permissions · historique complet.

```
ENTREPÔT A → TRANSFERT → EN TRANSIT → ENTREPÔT B
```

## 15. Inventaire

Complet / partiel / tournant · comptage par emplacement ou produit · double comptage selon config · calcul des écarts · validation · génération d'ajustements · audit.

## 16. Pertes, casse et expiration

Type · produit · lot si applicable · quantité · motif · utilisateur · approbation selon seuil · mouvement de stock · impact financier selon règle · historique.

## 17. Lots et traçabilité

Numéro de lot · fabrication · expiration · quantité par lot · emplacement · fournisseur · réception source · historique des mouvements.

## 18. Expiration et FEFO

Pour pharmacie et produits périssables : stratégie **FEFO** (First Expired, First Out).

- Identifier les lots les plus proches de l'expiration.
- Prioriser ces lots dans les sorties.
- Alerter avant expiration.
- Bloquer ou autoriser la vente selon politique.
- Traçabilité du lot vendu.

## 19. Réapprovisionnement

Stock minimum · point de commande · stock maximum · délai fournisseur · ventes historiques · suggestions de quantité · commande automatique si activée · validation humaine selon seuil · alertes de rupture.

## 20. Fournisseurs

Fiche · contacts · adresses · identifiant fiscal · conditions de paiement · limite de crédit · devise · produits fournis · historique achats/paiements · solde dû · échéances.

## 21. Cycle d'achat

```
BESOIN → DEMANDE D'ACHAT → APPROBATION → COMMANDE FOURNISSEUR
       → RÉCEPTION → CONTRÔLE → FACTURE → PAIEMENT OU DETTE → COMPTABILITÉ
```

## 22. Demande d'achat

Créateur · établissement · produits · quantités · motif · priorité · date souhaitée · budget éventuel · approbation.

## 23. Commande fournisseur

Numéro · fournisseur · date · produits · quantités · prix négociés · taxes · remises · devise · date prévue · conditions de paiement · statut.

## 24. Réception

Complète ou partielle · contrôle quantitatif · contrôle qualité selon secteur · écart commande/réception · lots et expiration · emplacement de réception · bon de réception · validation · mise à jour du stock.

```
RÉCEPTION VALIDÉE → STOCK ↑
```

## 25. Facture fournisseur

Numéro fournisseur · date · échéance · lignes · taxes · remises · total · devise · commande/réception source · statut.

## 26. Achat comptant

```
FACTURE → PAIEMENT → FINANCE ↓ → STOCK ↑ → COMPTABILITÉ
```

La facture et le paiement restent traçables séparément.

## 27. Achat à crédit

```
FACTURE → DETTE FOURNISSEUR ↑ → STOCK ↑ → COMPTABILITÉ
PAIEMENT PARTIEL → DETTE ↓ + TRÉSORERIE ↓
PAIEMENT FINAL → DETTE = 0
```

Échéance · paiement partiel · paiements multiples · retard · limite de crédit · relevé fournisseur · alertes · historique.

## 28. Retours fournisseur

Sélectionner facture/réception · produits et quantités · motif · lot si applicable · validation · sortie stock · avoir fournisseur · impact dette/finance/comptabilité.

## 29. Contrôle des coûts

Comparer prix commandé / reçu / facturé · détecter écarts · historiser le coût · calculer coût moyen selon méthode · signaler variation anormale · approbation si seuil dépassé.

## 30. Valorisation du stock

Méthode choisie par entreprise, cohérente avec les règles comptables : coût moyen pondéré · FIFO lorsque retenu · autre méthode autorisée. La méthode est documentée et ne change pas rétroactivement les historiques sans procédure contrôlée.

## 31. Produits spécialisés — Pharmacie

Lot obligatoire lorsque pertinent · expiration · FEFO · dosage · forme · fabricant · traçabilité des sorties · alertes de péremption · règles de vente selon réglementation.

## 32. Produits spécialisés — Restaurant

Ingrédients · recettes · unités de consommation · sous-recettes · coût de revient · consommation automatique à la production/vente · pertes et gaspillage.

## 33. Produits spécialisés — Supermarché

Produits au poids · balance/barcode · prix par unité · promotions · multi-caisses · inventaire rapide · produits à forte rotation · étiquettes prix.

## 34. Produits spécialisés — Grossiste

Tarifs par quantité · conditionnements · clients professionnels · commandes volumineuses · stock réservé · livraison · crédit client.

## 35. Alertes stock et achats

Rupture imminente · stock sous minimum · stock excessif · expiration proche · variation de prix fournisseur · commande en retard · réception incomplète · facture non reçue · dette à échéance · produit sans fournisseur actif.

## 36. Permissions

Voir catalogue · créer/modifier produit · modifier prix · gérer fournisseurs · créer demande d'achat · approuver demande · créer commande · valider réception · modifier stock · faire inventaire · valider ajustement · créer retour fournisseur · voir coûts · exporter.

Toujours combinées au périmètre entreprise/établissement/magasin/entrepôt.

## 37. Audit

Création/modification produit · modification de prix · ajustement stock · inventaire validé · réception · commande · facture · retour · annulation · changement de fournisseur · modification des seuils.

## 38. Intégration avec Finance & Comptabilité

```
ACHAT → RÉCEPTION → STOCK
      → FACTURE → DETTE OU PAIEMENT → FINANCE → COMPTABILITÉ

RETOUR FOURNISSEUR → STOCK ↓ → AVOIR / DETTE ↓ → FINANCE / COMPTABILITÉ
```

## 39. Scénarios de validation

Créer un produit (SKU + code-barres) · associer deux fournisseurs · réception partielle · réception avec lot et expiration · vente → sortie de stock · transfert entre entrepôts · inventaire → écart validé · perte/casse · achat comptant · achat à crédit + paiement partiel · règlement dette fournisseur · retour fournisseur · alerte de stock faible · FEFO sur plusieurs lots · utilisateur hors périmètre ne voit pas le stock · traçabilité complète d'une opération.

## 40. Critères de sortie de la Phase 5

- Catalogue produit défini.
- Prix et coûts historisés.
- Stock multi-emplacements défini.
- Mouvements de stock traçables.
- Inventaires et ajustements définis.
- Lots/expiration définis.
- Réapprovisionnement défini.
- Cycle fournisseur défini.
- Achats comptants et à crédit définis.
- Retours fournisseur définis.
- Intégration avec finance/comptabilité définie.
- Permissions et audit définis.
- Scénarios critiques testables.
- Les secteurs spécialisés utilisent le noyau commun.

## 41. Étape suivante

Après validation, passer à la **Phase 6 — Ventes & POS**. Le POS exploitera le catalogue, le stock et le moteur financier afin qu'une vente soit enregistrée une seule fois puis propagée de manière cohérente vers stock, caisse, créances, finance et comptabilité.

---

**NEXORA — Phase 5 validée → Phase 6 : Ventes & POS**
