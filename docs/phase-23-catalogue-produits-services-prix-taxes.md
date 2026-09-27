# NEXORA — PHASE 23 — CATALOGUE, PRODUITS, SERVICES, PRIX & TAXES

**Moteur produit universel de NEXORA V1.0**
**Version 1.0**

---

## 1. Objectif

Créer un catalogue universel capable de représenter des marchandises, services, produits composés, produits pondérés, médicaments, menus, prestations et articles de gros dans un même moteur. L'entreprise doit pouvoir adapter le catalogue à son activité sans changer de version de NEXORA.

## 2. Principe universel

```
ITEM
├── PRODUCT
├── SERVICE
├── COMPOSITE / RECIPE
└── NON-STOCK ITEM
        ↓
PRICING
        ↓
TAX
        ↓
SALES / PURCHASES / INVENTORY
        ↓
FINANCE / ACCOUNTING
```

## 3. Types d'articles

- Produit stocké.
- Produit non stocké.
- Service.
- Produit composé/bundle.
- Recette/menu.
- Produit pondéré.
- Produit avec lot/expiration.
- Produit à variantes.
- Consommable.
- Article générique.

## 4. Fiche produit

- ID.
- Nom.
- Nom court POS.
- Description.
- SKU.
- Type.
- Catégorie.
- Marque.
- Unité de vente.
- Unité d'achat.
- Statut.
- Image.
- Référence fournisseur.
- Référence interne.
- Notes.
- Champs personnalisés.

## 5. Identité commerciale

- SKU unique selon périmètre.
- Code-barres multiples.
- Code interne.
- Code fournisseur.
- Code PLU pour produits pondérés lorsque nécessaire.
- Référence fabricant.
- Alias/recherche.

## 6. Codes-barres

- Plusieurs codes-barres par article.
- Type de code.
- Code principal.
- Code secondaire.
- Date d'activation.
- Statut.
- Validation unicité.
- Recherche instantanée POS.

## 7. Unités

```
UNIT
├── pièce
├── kg
├── g
├── litre
├── ml
├── m
├── boîte
├── paquet
└── unité personnalisée
```

- Unité de base.
- Unité de vente.
- Unité d'achat.
- Conversion.
- Précision configurable.
- Gestion des produits pondérés.

## 8. Conversions

```
1 CARTON = 12 BOÎTES = 144 PIÈCES
```

- Facteur conversion.
- Direction.
- Règle de précision.
- Arrondi.
- Historisation lorsque nécessaire.

## 9. Catégories

- Arborescence.
- Catégories imbriquées.
- Catégorie POS.
- Catégorie comptable.
- Catégorie stock.
- Catégorie reporting.
- Statut.
- Ordre affichage.

## 10. Marques

- Marque.
- Fabricant.
- Pays origine si nécessaire.
- Statut.
- Recherche.

## 11. Variantes

- Taille.
- Couleur.
- Format.
- Dosage.
- Conditionnement.
- Attributs personnalisés.
- SKU distinct.
- Code-barres distinct.

## 12. Produits pharmacie

- Nom commercial.
- DCI/générique lorsque pertinent.
- Dosage.
- Forme.
- Conditionnement.
- Lot.
- Expiration.
- Fabricant.
- Code produit.
- Alerte expiration.
- Règles de vente configurables selon réglementation locale.

NEXORA doit fournir des capacités de gestion, mais toute contrainte réglementaire spécifique doit être configurable et validée selon le pays et le secteur.

## 13. Produits restaurant

- Article vendu.
- Ingrédients.
- Recette.
- Unité.
- Prix.
- Options.
- Suppléments.
- Allergènes lorsque l'entreprise les renseigne.
- Production/déduction stock selon configuration.
- Menu/combo.

## 14. Produits supermarché

- Code-barres.
- Produit pesé.
- PLU.
- Prix au poids.
- Promotion.
- Emballage.
- Consigne lorsque nécessaire.
- Lots/expiration selon produit.
- Multi-prix.

## 15. Services

- Nom.
- Description.
- Durée optionnelle.
- Prix.
- Taxe.
- Catégorie.
- Commission éventuelle.
- Pas de mouvement stock.
- Compte comptable associé.

## 16. Produits composés

```
BUNDLE / COMBO
├── ITEM A × 1
├── ITEM B × 2
└── SERVICE × 1
```

- Composition.
- Quantité.
- Prix calculé ou fixe.
- Déduction stock.
- Recette.
- Disponibilité.

## 17. Recettes

- Recette versionnée.
- Ingrédients.
- Quantités.
- Unités.
- Pertes.
- Rendement.
- Coût théorique.
- Prix.
- Version active.
- Historique.

## 18. Prix

```
PRICE LIST
├── COST
├── RETAIL
├── WHOLESALE
├── MEMBER
├── PROMOTIONAL
└── CUSTOM
```

- Prix par entreprise.
- Prix par établissement.
- Prix par client/tarif.
- Prix par quantité.
- Date début.
- Date fin.
- Priorité.
- Devise.

## 19. Historique prix

Un prix utilisé par une transaction doit rester reconstituable. Les changements de prix futurs ne doivent pas réécrire le prix historique des ventes.

## 20. Prix par quantité

```
1–9      → PRIX A
10–49    → PRIX B
50+      → PRIX C
```

- Seuil.
- Prix.
- Date validité.
- Liste tarifaire.
- Devise.

## 21. Promotions

- Pourcentage.
- Montant fixe.
- Prix spécial.
- Deux pour un.
- Bundle.
- Remise par quantité.
- Période.
- Jour/heure.
- Client cible.
- Produit cible.
- Limite utilisation.
- Priorité.
- Cumul autorisé ou non.

## 22. Moteur de prix

```
PRODUCT
 ↓
BASE PRICE
 ↓
CUSTOMER / PRICE LIST
 ↓
QUANTITY
 ↓
PROMOTION
 ↓
DISCOUNT
 ↓
TAX
 ↓
FINAL PRICE
```

- Ordre des règles configurable.
- Résultat explicable au POS.
- Historique des règles appliquées.
- Aucune règle cachée.

## 23. Taxes

- Code.
- Nom.
- Taux.
- Type.
- Pays/juridiction.
- Compte comptable.
- Date début.
- Date fin.
- Statut.
- Exonération.
- Priorité.

## 24. Taxe par produit

- Taxe standard.
- Taxe réduite.
- Exonéré.
- Taxe multiple lorsque nécessaire.
- Règle locale.
- Historisation.

## 25. Prix TTC / HT

- Entreprise peut travailler TTC ou HT.
- Configuration explicite.
- Conversion déterministe.
- Arrondis définis.
- Ticket et facture affichent la base appropriée.
- Comptabilité utilise les montants cohérents avec les règles.

## 26. Arrondis

- Précision devise.
- Arrondi ligne.
- Arrondi facture.
- Arrondi paiement.
- Règle documentée.
- Tests limites.

## 27. Devises

- Devise fonctionnelle entreprise.
- Devise transaction.
- Taux de change.
- Date du taux.
- Source du taux.
- Conversion.
- Écart de change lorsque nécessaire.

## 28. Coût

- Coût d'achat.
- Coût moyen.
- Coût par lot.
- Coût standard.
- Dernier coût.
- Coût théorique recette.
- Méthode de valorisation définie par entreprise.

## 29. Fournisseurs liés

- Produit fournisseur.
- SKU fournisseur.
- Coût fournisseur.
- MOQ.
- Délai.
- Unité achat.
- Dernier prix.
- Fournisseur principal.
- Fournisseurs alternatifs.

## 30. Stock lié au produit

- Stockable/non stockable.
- Entrepôt autorisé.
- Seuil minimum.
- Seuil maximum.
- Point de commande.
- Stock de sécurité.
- Emplacement préféré.
- Gestion lot.
- Gestion expiration.
- FEFO activable.

## 31. Paramètres POS

- Visible POS.
- Favori.
- Catégorie POS.
- Bouton rapide.
- Couleur/icône selon design system.
- Raccourci.
- Quantité décimale.
- Prix modifiable selon permission.
- Remise autorisée.
- Vente à crédit autorisée.

## 32. Disponibilité

- Actif.
- Inactif.
- En rupture.
- Discontinué.
- À venir.
- Saisonnier.
- Disponible par établissement.

## 33. Catalogue multi-entreprises

```
CATALOG GLOBAL
      ↓
COMPANY CONFIG
      ↓
ESTABLISHMENT CONFIG
      ↓
POS CONFIG
```

- Produit partagé possible.
- Prix local.
- Taxe locale.
- Stock local.
- SKU local.
- Compte comptable local.
- Activation locale.

## 34. Import catalogue

- CSV/Excel selon format retenu.
- Validation avant import.
- Aperçu.
- Mapping colonnes.
- Détection doublons.
- Rapport erreurs.
- Import par lot.
- Rollback transactionnel lorsque possible.

## 35. Export catalogue

- Produits.
- Prix.
- Codes-barres.
- Taxes.
- Fournisseurs.
- Stock selon permission.
- Format standard.
- Filtres.

## 36. Import initial grande entreprise

- Catalogue massif.
- Traitement asynchrone.
- Progression.
- Erreurs par ligne.
- Reprise.
- Journal import.
- Validation totaux.

## 37. Recherche

- SKU.
- Code-barres.
- Nom.
- Alias.
- Marque.
- Catégorie.
- PLU.
- Référence fournisseur.
- Recherche rapide POS.
- Recherche tolérante.

## 38. Recherche POS

```
SCAN → EXACT MATCH → ADD TO CART
```

La recherche par code-barres doit privilégier une réponse immédiate et éviter des requêtes inutiles.

## 39. Fiche produit — écran gestion

- Informations générales.
- Prix.
- Taxes.
- Stock.
- Fournisseurs.
- Historique prix.
- Promotions.
- Codes-barres.
- Lots.
- Comptabilité.
- POS.
- Audit.

## 40. Fiche produit — POS

- Nom court.
- Image optionnelle.
- Prix.
- Unité.
- Disponibilité.
- Promotion.
- Quantité.
- Actions rapides.

## 41. Comptabilité produit

- Compte ventes.
- Compte achats.
- Compte stock.
- Compte variation stock lorsque nécessaire.
- Compte taxe.
- Compte remise.
- Compte coût selon configuration.

## 42. Champs personnalisés

- Nom.
- Type.
- Obligatoire.
- Valeur par entreprise.
- Valeur par produit.
- Validation.
- Visible POS ou non.
- Recherche ou non.
- Historique.

## 43. Attributs sectoriels

- Pharmacie : dosage, forme, lot, expiration.
- Restaurant : recette, ingrédients, allergènes.
- Supermarché : poids, PLU, emballage.
- Grossiste : carton, palette, MOQ.
- Services : durée, prestation.
- Production : matière, rendement, recette.

## 44. Numérotation

- SKU automatique ou manuel.
- Référence interne.
- Code fournisseur.
- Règle par entreprise.
- Préfixe établissement.
- Séquence sûre en concurrence.
- Audit des modifications.

## 45. Workflow produit

```
BROUILLON
 ↓
À VALIDER
 ↓
ACTIF
 ↓
SUSPENDU
 ↓
ARCHIVÉ
```

- Workflow configurable.
- Approbation si entreprise l'exige.
- Audit.

## 46. Sécurité

- Seules permissions appropriées modifient prix.
- Modification taxe contrôlée.
- Modification compte comptable contrôlée.
- Import massif contrôlé.
- Suppression physique limitée.
- Audit des modifications sensibles.

## 47. API principales

```
GET/POST    /api/v1/products
GET/PATCH   /api/v1/products/{id}
GET/POST    /api/v1/products/{id}/barcodes
GET/POST    /api/v1/products/{id}/prices
GET/POST    /api/v1/products/{id}/suppliers
GET/POST    /api/v1/categories
GET/POST    /api/v1/price-lists
GET/POST    /api/v1/promotions
GET/POST    /api/v1/taxes
GET         /api/v1/catalog/search
```

## 48. Tests

- Produit simple.
- Service.
- Produit composé.
- Recette.
- Produit pondéré.
- Code-barres multiple.
- Prix historique.
- Prix quantité.
- Promotion.
- Taxe HT/TTC.
- Multi-devise.
- Produit pharmacie.
- Produit restaurant.
- Produit supermarché.
- Produit multi-entreprise.
- Import massif.
- Recherche POS.

## 49. Critères de sortie

- Catalogue opérationnel.
- Types articles définis.
- Codes-barres opérationnels.
- Unités et conversions.
- Prix et historique.
- Promotions.
- Taxes.
- Multi-devise.
- Fournisseurs liés.
- Stock configurable.
- POS configurable.
- Comptabilité produit.
- Champs personnalisés.
- Import/export.
- Recherche.
- API.
- Permissions.
- Audit.
- Tests.

## 50. Étape suivante

Passer à la **Phase 24 — Gestion des stocks, entrepôts, lots, expiration, inventaires, transferts et valorisation**. Elle transformera le catalogue en véritable moteur de stock multi-secteurs, avec gestion adaptée aux commerces, pharmacies, restaurants, grossistes et entreprises de production.

---

**NEXORA V1.0 — PHASE 23 VALIDÉE → PHASE 24 : STOCK & INVENTAIRE**
