# NEXORA — PHASE 31 — REPORTING & ANALYTICS

**Tableaux de bord, KPI, analyses commerciales, financières & opérationnelles NEXORA V1.0**
**Version 1.0**

## 1. Objectif

Transformer les données opérationnelles, financières et comptables de NEXORA en informations utiles à la décision. Les tableaux de bord doivent être adaptés au rôle de l'utilisateur, au périmètre autorisé et au secteur d'activité, depuis une petite entreprise jusqu'à un groupe multi-entreprises.

## 2. Principe

```
DONNÉES NEXORA
       ↓
INDICATEURS
       ↓
AGRÉGATIONS
       ↓
DASHBOARDS / RAPPORTS
       ↓
ANALYSE
       ↓
ACTION / DÉCISION
```

- Un indicateur doit avoir une définition claire.
- Les données doivent respecter le périmètre d'accès.
- Les chiffres financiers importants doivent être réconciliables.
- Les périodes et filtres doivent être explicites.
- Les rapports historiques doivent pouvoir être reproduits.

## 3. Périmètres

- Groupe.
- Entreprise.
- Établissement.
- Entrepôt.
- Caisse.
- Département.
- Produit.
- Catégorie.
- Commercial.
- Utilisateur.
- Client.
- Fournisseur.
- Projet.

## 4. Dashboard direction

- Chiffre d'affaires.
- Marge.
- Résultat.
- Trésorerie.
- Créances.
- Dettes.
- Stock.
- Dépenses.
- Budget.
- Alertes.
- Comparaison entreprises.
- Tendances.

## 5. Dashboard groupe

```
ENTREPRISE A ─┐
ENTREPRISE B ─┼→ KPI GROUPE
ENTREPRISE C ─┘
       ↓
CONSOLIDATION
       ↓
COMPARAISON / TENDANCES
```

- CA par entreprise.
- Résultat.
- Marge.
- Trésorerie.
- Créances/dettes.
- Stock.
- Flux intercompany.
- Budget.
- Anomalies.

## 6. Dashboard manager

- Ventes du jour.
- Objectif.
- Panier moyen.
- Nombre transactions.
- Top produits.
- Stock critique.
- Retours.
- Remises.
- Performance caisses.
- Performance équipe.
- Alertes.

## 7. Dashboard caissier

- Ventes session.
- Montant encaissé.
- Nombre transactions.
- Remboursements autorisés.
- État caisse.
- Alertes.
- Objectifs si configurés.

## 8. Dashboard comptable

- Balance.
- Grand livre.
- Créances.
- Dettes.
- Taxes.
- Banques.
- Caisses.
- Suspense.
- Écritures en attente.
- Périodes.
- Contrôles.
- Clôture.

## 9. Dashboard finance

- Position trésorerie.
- Entrées.
- Sorties.
- Prévisions.
- Échéances clients.
- Échéances fournisseurs.
- Budgets.
- Écarts.
- Rapprochements.
- Liquidité.

## 10. Dashboard stock

- Valeur stock.
- Quantité.
- Ruptures.
- Surstock.
- Rotation.
- Produits dormants.
- Expiration.
- Lots.
- Inventaires.
- Écarts.
- Transferts.
- Stock en transit.

## 11. Ventes

- CA.
- Nombre ventes.
- Unités vendues.
- Panier moyen.
- Marge.
- Marge %.
- Remises.
- Taxes.
- Retours.
- Annulations.
- Ventes crédit.
- Encaissements.
- Évolution.

## 12. CA

```
CA = SOMME DES VENTES NETTES SELON PÉRIMÈTRE ET PÉRIODE
```

La définition exacte doit préciser retours, avoirs, taxes et autres éléments inclus/exclus.

## 13. Panier moyen

```
PANIER MOYEN = CA NET / NOMBRE DE TRANSACTIONS
```

## 14. Marge

```
MARGE = VENTES NETTES − COÛT DES VENTES
```

- Marge par produit.
- Catégorie.
- Entreprise.
- Établissement.
- Client.
- Période.

## 15. Analyse produits

- Top ventes.
- Top marge.
- Faible rotation.
- Ruptures fréquentes.
- Produits promotionnels.
- Produits à faible marge.
- Produits proches expiration.
- Évolution prix/coût.

## 16. Analyse clients

- CA client.
- Marge client.
- Fréquence.
- Panier moyen.
- Créances.
- Retards.
- Top clients.
- Clients inactifs.
- Concentration.

## 17. Analyse fournisseurs

- Achats.
- Délai.
- Prix moyen.
- Retards.
- Taux livraison complète.
- Retours.
- Litiges.
- Dépendance.
- Évolution.

## 18. Analyse caisses

- CA par caisse.
- Transactions.
- Panier moyen.
- Espèces.
- Carte.
- Mobile Money.
- Écarts.
- Remboursements.
- Annulations.
- Sessions.

## 19. Analyse employés

- Ventes.
- Transactions.
- Panier.
- Remises.
- Retours.
- Objectifs.
- Commissions selon configuration.
- Anomalies.

## 20. Crédit client

- Créances totales.
- Courant.
- Échu.
- Aging.
- DSO.
- Top débiteurs.
- Limite utilisée.
- Retards.
- Recouvrement.
- Promesses.

## 21. Dettes fournisseurs

- Total dettes.
- À payer.
- En retard.
- Échéances.
- Aging.
- Top fournisseurs.
- Paiements prévus.
- Concentration.

## 22. Trésorerie

- Solde caisse.
- Banque.
- Mobile Money.
- Total disponible.
- Entrées/sorties.
- Net cash flow.
- Prévisions.
- Écart prévu/réel.

## 23. Budget

```
VARIANCE = RÉEL − BUDGET
```

- Variance montant.
- Variance %.
- Engagé.
- Disponible.
- Dépassement.
- Tendance.
- Par entreprise.
- Par département.
- Par catégorie.

## 24. Comptabilité

- Balance.
- Grand livre.
- Bilan.
- Compte de résultat.
- Flux de trésorerie.
- Charges.
- Produits.
- Taxes.
- Immobilisations.
- Analytique.
- Intercompany.

## 25. KPI stock

- Stock disponible.
- Stock moyen.
- Rotation.
- Jours de stock.
- Rupture.
- Surstock.
- Valeur.
- Casse.
- Pertes.
- Expiration.
- Écarts inventaire.

## 26. Rotation stock

NEXORA doit permettre plusieurs méthodes de calcul de rotation, avec définition affichée dans le rapport afin d'éviter toute interprétation ambiguë.

## 27. KPI opérationnels

- Temps de préparation commande.
- Temps de traitement caisse.
- Taux rupture.
- Taux retour.
- Taux annulation.
- Taux paiement échoué.
- Taux rapprochement.
- Temps de résolution suspense.
- Temps résolution litige.

## 28. Restaurant

- CA par table.
- CA par service.
- Articles vendus.
- Food cost.
- Marge menu.
- Temps préparation.
- Annulations cuisine.
- Pertes.
- Pourboires si activés.

## 29. Pharmacie

- Ventes par catégorie.
- Produits proches expiration.
- Lots expirants.
- Rotation.
- Ruptures.
- Valeur stock.
- Retours.
- Ventes par établissement.

## 30. Supermarché

- CA par rayon.
- Unités.
- Prix moyen.
- Promotions.
- Marge rayon.
- Ruptures.
- Casse.
- Produits pondérés.
- Performance caisse.

## 31. Grossiste

- CA gros.
- Volume.
- Palettes/cartons.
- Marge.
- Commandes.
- Livraisons.
- Créances.
- Retards.
- Performance fournisseurs.

## 32. Services

- CA prestation.
- Nombre prestations.
- Durée.
- Taux occupation lorsque applicable.
- Marge.
- Clients.
- Factures impayées.
- Performance prestataires.

## 33. Rapports planifiés

- Quotidien.
- Hebdomadaire.
- Mensuel.
- Trimestriel.
- Annuel.
- Date/heure.
- Destinataires autorisés.
- Format.
- Filtres.

## 34. Rapports personnalisés

- Choix métriques.
- Dimensions.
- Filtres.
- Groupements.
- Colonnes.
- Tri.
- Période.
- Comparaison.
- Sauvegarde modèle.
- Partage contrôlé.

## 35. Drill-down

```
KPI CA
 ↓
ENTREPRISE
 ↓
ÉTABLISSEMENT
 ↓
CAISSE
 ↓
JOUR
 ↓
TRANSACTION
 ↓
LIGNE
```

- Chaque niveau respecte les permissions.
- La transaction source doit être consultable si autorisée.

## 36. Comparaisons

- Jour vs jour.
- Semaine vs semaine.
- Mois vs mois.
- Année vs année.
- Budget vs réel.
- Entreprise A vs B.
- Période précédente.
- Moyenne historique.

## 37. Tendances

- Courbes.
- Barres.
- Tableaux.
- KPI cards.
- Heatmaps lorsque pertinentes.
- Indicateurs d'évolution.
- Objectifs.

## 38. Alertes

- Rupture.
- Surstock.
- Expiration.
- CA en baisse.
- Marge en baisse.
- Créances en retard.
- Dette proche échéance.
- Budget dépassé.
- Écart caisse.
- Rapprochement en retard.
- Anomalie.

## 39. Alertes intelligentes

- Seuil fixe.
- Variation %.
- Comparaison historique.
- Anomalie statistique optionnelle.
- Priorité.
- Responsable.
- Acquittement.
- Historique.

Les alertes analytiques doivent rester explicables et ne doivent pas être présentées comme des décisions automatiques.

## 40. Objectifs

- Objectif CA.
- Marge.
- Nombre transactions.
- Recouvrement.
- Rotation stock.
- Réduction pertes.
- Budget.
- Objectif par entreprise.
- Établissement.
- Équipe.
- Utilisateur.

## 41. Scorecards

```
OBJECTIF
  │
RÉEL
  │
ÉCART
  │
STATUT
  │
ACTION
```

## 42. Exports

- PDF.
- CSV.
- Excel.
- Image lorsque pertinent.
- Données brutes selon permission.
- Rapport formaté.
- Horodatage export.
- Utilisateur.

## 43. Partage

- Lien sécurisé.
- Expiration.
- Permission lecture.
- Destinataires.
- Export contrôlé.
- Révocation.

## 44. Snapshots

- Photo KPI à une date.
- Période.
- Périmètre.
- Version calcul.
- Utilisateur.
- Reproductibilité.

## 45. Data quality

- Données manquantes.
- Doublons.
- Transactions non rapprochées.
- Écritures non validées.
- Produits sans coût.
- Clients sans conditions.
- Stocks négatifs.
- Erreurs de mapping.
- Score qualité.

## 46. Réconciliation KPI

- CA reporting = ventes source.
- Encaissement = paiements confirmés.
- Stock reporting = ledger stock.
- Résultat = comptabilité.
- Créances = comptes clients.
- Dettes = comptes fournisseurs.

## 47. Performance

- Pagination.
- Agrégations optimisées.
- Cache contrôlé.
- Pré-calcul pour gros volumes.
- Traitements asynchrones.
- Filtres indexés.
- Séparation données temps réel / historiques si nécessaire.

## 48. Temps réel

- Ventes POS récentes.
- Caisse.
- Stock critique.
- Paiements.
- Alertes.
- Position trésorerie selon fréquence de rafraîchissement.

## 49. Sécurité

- Scope appliqué à chaque requête.
- Aucune donnée d'une autre entreprise.
- Exports protégés.
- Dashboards respectent permissions.
- Audit des rapports sensibles.
- Partage avec expiration.

## 50. API principales

```
GET  /api/v1/dashboards
GET  /api/v1/kpis
GET  /api/v1/reports
POST /api/v1/reports
PATCH /api/v1/reports/{id}
POST /api/v1/reports/{id}/run
POST /api/v1/reports/{id}/schedule
GET  /api/v1/analytics/sales
GET  /api/v1/analytics/finance
GET  /api/v1/analytics/stock
GET  /api/v1/analytics/accounting
GET  /api/v1/analytics/customers
GET  /api/v1/analytics/suppliers
GET  /api/v1/alerts
POST /api/v1/alerts/{id}/acknowledge
```

## 51. Permissions

- Voir dashboard.
- Voir finance.
- Voir comptabilité.
- Voir coûts.
- Voir salaires si module futur et autorisé.
- Créer rapport.
- Exporter.
- Planifier.
- Partager.
- Voir groupe.
- Voir données brutes.

## 52. Audit

- Création rapport.
- Modification.
- Exécution.
- Export.
- Partage.
- Accès données sensibles.
- Modification KPI.
- Acquittement alerte.

## 53. Tests

- KPI CA.
- Marge.
- Panier.
- Stock.
- Créances.
- Dettes.
- Trésorerie.
- Budget.
- Balance.
- Bilan.
- Résultat.
- Groupe.
- Drill-down.
- Filtres.
- Comparaisons.
- Exports.
- Planification.
- Permissions.
- Isolation.

## 54. Scénario direction

```
GROUPE
 ↓
CA 12 MOIS
 ↓
COMPARAISON ENTREPRISES
 ↓
MARGE
 ↓
TRÉSORERIE
 ↓
CRÉANCES / DETTES
 ↓
STOCK
 ↓
ANOMALIES
 ↓
DRILL-DOWN
 ↓
ACTION
```

## 55. Critères de sortie

- Dashboards rôle.
- Dashboard groupe.
- Ventes.
- Produits.
- Clients.
- Fournisseurs.
- Caisses.
- Crédit.
- Dettes.
- Trésorerie.
- Budget.
- Comptabilité.
- Stock.
- KPI secteur.
- Rapports personnalisés.
- Drill-down.
- Comparaisons.
- Tendances.
- Alertes.
- Objectifs.
- Exports.
- Planification.
- Partage.
- Snapshots.
- Data quality.
- Réconciliation.
- Performance.
- Sécurité.
- Permissions.
- Audit.
- API.
- Tests.

## 56. Étape suivante

Passer à la Phase 32 — Paramétrage, personnalisation et moteur d'adaptation par secteur. Elle permettra à NEXORA de conserver toutes ses fonctionnalités tout en laissant chaque entreprise configurer son environnement, ses workflows, ses écrans, ses règles et ses modules sans installer une version différente.

---

**NEXORA V1.0 — PHASE 31 VALIDÉE → PHASE 32 : CONFIGURATION & ADAPTABILITÉ**
