# NEXORA — PHASE 8 — MODULES SECTORIELS

**Adaptation universelle de NEXORA aux différents secteurs d'activité**
Version 1.0

## 1. Objectif

Permettre à une même installation NEXORA de gérer plusieurs types d'entreprises — pharmacie,
restaurant, supermarché, boutique, grossiste, distribution, services, production et autres —
sans créer une version différente du logiciel. Le noyau commercial, financier, comptable et
organisationnel reste commun ; les modules sectoriels ajoutent uniquement les processus
propres à chaque métier.

## 2. Principe architectural

```
NEXORA CORE
├── Organisation
├── Utilisateurs / Accès
├── Produits
├── Stock
├── Achats
├── Ventes / POS
├── Clients / Fournisseurs
├── Finance
├── Comptabilité
└── Rapports
        │
        ├── PHARMACIE
        ├── RESTAURANT
        ├── SUPERMARCHÉ / RETAIL
        ├── GROSSISTE / DISTRIBUTION
        ├── SERVICES
        ├── PRODUCTION
        └── AUTRES MÉTIERS
```

## 3. Règles d'adaptation

- Une entreprise choisit son activité principale lors de la configuration.
- Elle peut activer plusieurs activités ou modules si nécessaire.
- Les modules peuvent être activés/désactivés selon les droits.
- Changer d'activité ne doit pas supprimer les données existantes.
- Les mêmes produits, clients, paiements, stocks et écritures utilisent le même noyau.
- Les champs spécialisés ne doivent apparaître que lorsqu'ils sont pertinents.
- Une nouvelle activité doit pouvoir être ajoutée sans réécrire le Core.

## 4. Configuration sectorielle

- Type d'activité.
- Modules activés.
- Processus activés.
- Champs spécialisés.
- Unités.
- Taxes.
- Règles de prix.
- Règles de stock.
- Workflows.
- Documents.
- Permissions.
- Terminologie affichée.

Exemple : dans un restaurant, le terme « commande » peut être privilégié ; dans un grossiste,
« commande client » et « bon de livraison » peuvent être prioritaires, tout en utilisant les
mêmes mécanismes transactionnels.

## 5. Module Pharmacie — objectif

Gérer les particularités d'une pharmacie ou d'un commerce de produits de santé, sous réserve
des lois et obligations locales applicables.

### Référentiel produit pharmacie

- Nom commercial.
- Dénomination/générique si applicable.
- Dosage.
- Forme.
- Conditionnement.
- Fabricant.
- Laboratoire.
- Code produit.
- Lot.
- Expiration.
- Informations réglementaires configurables.

### Lots et traçabilité

- Lot obligatoire pour les produits concernés.
- Quantité par lot.
- Fournisseur.
- Date de réception.
- Expiration.
- Emplacement.
- Historique des sorties.
- Traçabilité jusqu'à la vente lorsque requis.

### FEFO

- Prioriser le lot dont l'expiration est la plus proche.
- Alerter avant expiration.
- Bloquer la sortie de produits expirés selon politique.
- Permettre une dérogation uniquement avec permission appropriée.
- Conserver le lot associé à la vente.

### Prescriptions / ordonnances

- Enregistrer une référence d'ordonnance lorsque nécessaire.
- Associer les produits concernés.
- Utilisateur ayant effectué l'opération.
- Informations minimales nécessaires.
- Accès fortement restreint.
- Historique et audit.

Les données médicales ou réglementées doivent être limitées au strict nécessaire et protégées
selon le cadre légal applicable.

### Alertes pharmacie

- Expiration proche.
- Produit expiré.
- Stock critique.
- Lot rappelé.
- Variation de prix.
- Produit sans fournisseur.
- Anomalie de traçabilité.

## 6. Restaurant — objectif

Transformer NEXORA en système de prise de commande, caisse, cuisine, stock et gestion
financière pour restaurants, cafés, bars et établissements similaires.

### Tables

- Créer une table.
- Zones/salles.
- Nombre de places.
- Statut disponible/occupée/réservée.
- Déplacement d'une table.
- Fusion de tables.
- Séparation de tables.

### Commande

- Prise de commande.
- Serveur.
- Table.
- Articles.
- Modificateurs.
- Notes.
- Priorité.
- Statut.
- Transfert de commande.

### Cuisine / KDS

- Envoi automatique des articles concernés.
- Écran cuisine.
- Statuts à préparer/en préparation/prêt/servi.
- Priorités.
- Postes cuisine.
- Temps de préparation.
- Notification du serveur.

### Recettes et ingrédients

- Recette par produit vendu.
- Ingrédients.
- Quantités.
- Unités.
- Coût de revient.
- Sous-recettes.
- Consommation de stock.
- Pertes/gaspillage.

```
VENTE PLAT → CONSOMMATION INGREDIENTS → STOCK ↓ → COÛT → MARGE
```

### Paiement restaurant

- Addition complète.
- Paiement partiel.
- Division par personne.
- Division par article.
- Paiements multiples.
- Pourboire.
- Avoir/remboursement selon politique.

### Livraison

- Commande livraison.
- Adresse.
- Client.
- Frais.
- Livreur.
- Statut.
- Paiement.
- Bon de livraison.

## 7. Supermarché / Retail — objectif

Gérer un grand volume de produits et de transactions avec une expérience de caisse très rapide.

### POS

- Scanner haute fréquence.
- Raccourcis clavier.
- Produits favoris.
- Plusieurs caisses.
- Gestion des files si activée.
- Réimpression contrôlée.
- Retours rapides.

### Produits au poids

- Balance.
- Poids décimal.
- Prix/kg ou unité.
- Code-barres poids.
- Tare lorsque périphérique compatible.
- Contrôle du prix.

### Promotions

- Multi-buy.
- Deux pour un.
- Prix par quantité.
- Prix membre.
- Coupons.
- Promotion par catégorie.
- Période.
- Limite de cumul.

### Étiquetage

- Étiquette produit.
- Prix.
- Code-barres.
- Lot/expiration si applicable.
- Impression en masse.

## 8. Grossiste / Distribution — objectif

Gérer les ventes B2B, tarifs par volume, commandes, crédit, livraison et représentants.

- Clients professionnels.
- Tarifs par client/groupe.
- Tarifs par quantité.
- Commandes clients.
- Bon de livraison.
- Facturation différée.
- Crédit client.
- Limite de crédit.
- Représentants.
- Territoires.
- Livraisons.
- Retours.

```
COMMANDE → PRÉPARATION → LIVRAISON → FACTURE → CRÉANCE → PAIEMENT
```

## 9. Boutique / Commerce spécialisé

- Catalogue.
- Variantes.
- Tailles/couleurs.
- Code-barres.
- Fidélité.
- Promotions.
- Stock magasin.
- Retours.
- Caisse.
- Vente à crédit selon politique.

## 10. Services

NEXORA doit pouvoir gérer une activité où le produit vendu n'est pas nécessairement stockable.

- Catalogue de services.
- Tarifs.
- Durée.
- Prestataire.
- Rendez-vous si activé.
- Client.
- Facturation.
- Paiement.
- Crédit.
- Comptabilité.
- Commission.

## 11. Production — option évolutive

- Matières premières.
- Recettes/nomenclatures.
- Ordres de production.
- Consommation.
- Produit fini.
- Sous-produits.
- Pertes.
- Coût de revient.
- Stock matières.
- Stock produits finis.

```
MATIÈRES → PRODUCTION → PRODUIT FINI
STOCK MATIÈRES ↓ + STOCK FINI ↑
```

## 12. Hôtel / hébergement — option

- Chambres.
- Types de chambres.
- Réservation.
- Client.
- Check-in/check-out.
- Services consommés.
- Mini-bar.
- Facturation.
- Paiement.
- Crédit entreprise.
- Caisse.

## 13. E-commerce — option

- Catalogue web.
- Prix.
- Stock disponible.
- Commande en ligne.
- Client.
- Paiement.
- Préparation.
- Livraison.
- Retour.
- Synchronisation.
- Statut commande.

## 14. Agriculture / commerce de produits agricoles — option

- Produits au poids.
- Lots.
- Origine.
- Date de récolte.
- Qualité.
- Stock.
- Pertes.
- Fournisseurs.
- Ventes.
- Traçabilité.

## 15. Personnalisation pour activité inconnue

Pour une entreprise dont le secteur n'est pas encore couvert, NEXORA doit offrir un mode
générique configurable.

- Produits/services personnalisés.
- Champs personnalisés.
- Statuts personnalisés.
- Documents configurables.
- Workflows configurables.
- Catégories.
- Taxes.
- Prix.
- Stock si nécessaire.
- Rapports personnalisés.

L'objectif est d'éviter de bloquer l'entreprise parce que son activité n'existe pas dans une
liste prédéfinie.

## 16. Moteur de champs personnalisés

- Champ texte.
- Nombre.
- Date.
- Liste.
- Booléen.
- Référence à une entité.
- Fichier.
- Champ obligatoire ou facultatif.
- Règles de visibilité.
- Permissions.

```
ENTITY + CUSTOM_FIELDS → ADAPTATION SANS MODIFIER LE CORE
```

## 17. Workflows sectoriels

- Workflow de validation configurable.
- Étapes.
- Rôles responsables.
- Seuils.
- Délais.
- Actions automatiques.
- Notifications.
- Historique.

## 18. Terminologie adaptable

- Libellés métier personnalisables.
- Nom du document.
- Nom du statut.
- Nom de certaines entités.
- Unités.
- Traductions.
- Langue par utilisateur.

La personnalisation de vocabulaire ne doit pas changer le sens des transactions.

## 19. Documents sectoriels

- Pharmacie : documents et étiquettes nécessaires selon réglementation.
- Restaurant : ticket, addition, bon cuisine, livraison.
- Retail : ticket, étiquette.
- Grossiste : devis, commande, bon livraison, facture.
- Services : devis, facture, reçu.
- Production : ordre de production, fiche de fabrication.

## 20. Permissions sectorielles

- Activer/désactiver module.
- Voir données spécialisées.
- Créer.
- Modifier brouillon.
- Valider.
- Annuler.
- Exporter.
- Accéder aux données sensibles.
- Administrer configuration.

Les permissions spécialisées héritent du modèle global Groupe → Entreprise → Établissement →
Magasin/Entrepôt → Caisse.

## 21. Données sensibles

- Identifier les modules contenant des données sensibles.
- Limiter l'accès par rôle.
- Limiter par entreprise et établissement.
- Journaliser les consultations sensibles.
- Éviter les données inutiles.
- Protéger les exports.
- Définir les durées de conservation selon obligations applicables.

## 22. Intégration Core ↔ Sectoriel

```
SECTEUR
  ↓
MODULE SPÉCIALISÉ
  ↓
NEXORA CORE
  ├── Produit / Client / Fournisseur
  ├── Stock
  ├── Vente / Achat
  ├── Paiement
  ├── Finance
  └── Comptabilité
```

Un module sectoriel ne doit pas créer un système de paiement, de stock ou de comptabilité
parallèle.

## 23. Exemple : pharmacie

```
LOT + EXPIRATION → POS → VENTE → STOCK LOT ↓ → PAIEMENT/CRÉANCE → COMPTABILITÉ
```

## 24. Exemple : restaurant

```
TABLE → COMMANDE → KDS → SERVICE → PAIEMENT → STOCK INGREDIENTS ↓ → FINANCE/COMPTABILITÉ
```

## 25. Exemple : supermarché

```
SCAN → PANIER → PROMOTION → PAIEMENT → STOCK ↓ → CAISSE → COMPTABILITÉ
```

## 26. Exemple : grossiste

```
COMMANDE CLIENT → PRÉPARATION → LIVRAISON → FACTURE → CRÉANCE → PAIEMENT
```

## 27. Multi-activité

Une même entreprise peut combiner plusieurs activités.

- Exemple : supermarché + pharmacie.
- Restaurant + livraison.
- Grossiste + boutique.
- Hôtel + restaurant.
- Groupe avec pharmacie + restaurant + commerce.

Les activités partagent l'identité de l'entreprise mais peuvent disposer de magasins, stocks,
tarifs, caisses, utilisateurs et rapports distincts.

## 28. Configuration au démarrage

```
1. CRÉER ENTREPRISE
2. CHOISIR ACTIVITÉ(S)
3. ACTIVER MODULES
4. CONFIGURER TAXES / DEVISE / PRIX
5. CONFIGURER STOCK
6. CONFIGURER POS
7. CONFIGURER UTILISATEURS
8. CONFIGURER WORKFLOWS
9. COMMENCER L'ACTIVITÉ
```

## 29. Modification à tout moment

- Ajouter une activité.
- Activer un module.
- Désactiver un module pour les nouveaux processus.
- Modifier paramètres.
- Ajouter établissement.
- Ajouter caisse.
- Ajouter entrepôt.
- Ajouter utilisateurs.
- Modifier workflows.

La désactivation ne doit pas supprimer l'historique. Les données historiques restent
consultables selon les droits.

## 30. Rapports sectoriels

- Pharmacie : ventes par lot, expiration, stock, produits sensibles selon cadre.
- Restaurant : ventes par table, serveur, plat, coût ingrédients, gaspillage.
- Supermarché : ventes par caisse, produit, promotion, poids.
- Grossiste : ventes B2B, commandes, livraison, créances.
- Services : chiffre d'affaires par prestation et prestataire.
- Production : production, consommation, coût de revient.

## 31. Scénarios de validation

- Créer une entreprise pharmacie et activer lots/FEFO.
- Créer une entreprise restaurant avec tables et KDS.
- Créer une entreprise supermarché avec plusieurs caisses.
- Créer une entreprise grossiste avec tarifs par quantité.
- Créer une entreprise de services sans stock.
- Créer une entreprise production avec matières et produits finis.
- Créer un groupe contenant plusieurs secteurs.
- Activer deux secteurs dans une même entreprise.
- Ajouter un champ personnalisé.
- Modifier la configuration sans supprimer l'historique.
- Vérifier que les modules utilisent le même paiement/finance/comptabilité.
- Vérifier qu'un utilisateur ne voit pas les données sectorielles hors périmètre.

## 32. Critères de sortie

- Architecture Core/Sectoriel validée.
- Pharmacie définie.
- Restaurant défini.
- Supermarché/Retail défini.
- Grossiste/Distribution défini.
- Boutique défini.
- Services défini.
- Production définie comme extension.
- Hôtel et e-commerce identifiés comme extensions.
- Mode générique défini.
- Champs personnalisés définis.
- Workflows sectoriels définis.
- Permissions définies.
- Données sensibles traitées.
- Multi-activité défini.
- Intégration Core/sectoriel définie.
- Scénarios testables.

## 33. Étape suivante

Passer à la **Phase 9 — Multi-entreprises, Gestion de Groupe & Consolidation**. Elle détaillera
la gouvernance d'un groupe contenant plusieurs entreprises, les accès du comptable en chef, les
vues consolidées, les transactions intercompany et les règles de séparation des données.

---

**NEXORA — Phase 8 validée → Phase 9 : Multi-entreprises & Consolidation**
