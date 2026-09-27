# NEXORA — PHASE 29 — COMPTABILITÉ GÉNÉRALE

**Moteur comptable intégré, multi-entreprises et multi-devises NEXORA V1.0**
**Version 1.0**

## 1. Objectif

Construire un moteur de comptabilité générale fiable et traçable, connecté aux ventes, achats, stocks, créances, dettes et trésorerie. Il doit fonctionner pour une petite entreprise comme pour un groupe multi-entreprises, tout en permettant une consolidation selon les règles définies par le groupe.

## 2. Principe fondamental

```
ÉVÉNEMENT MÉTIER
      ↓
RÈGLE COMPTABLE
      ↓
JOURNAL
      ↓
ÉCRITURE
      ↓
LIGNES DÉBIT / CRÉDIT
      ↓
GRAND LIVRE
      ↓
BALANCE
      ↓
ÉTATS FINANCIERS
```

- Toute écriture doit être équilibrée.
- Les écritures validées ne sont pas supprimées silencieusement.
- Toute correction passe par une opération comptable traçable.
- La source métier doit rester identifiable.

## 3. Architecture comptable

```
ENTREPRISE
 ├── EXERCICE
 │    ├── PÉRIODES
 │    ├── JOURNAUX
 │    └── PLAN COMPTABLE
 └── ÉCRITURES
       └── LIGNES
```

## 4. Plan comptable

- Code.
- Libellé.
- Type.
- Classe.
- Compte parent.
- Compte auxiliaire.
- Actif/passif/charge/produit selon modèle.
- Statut.
- Compte système ou personnalisé.
- Date d'effet.

## 5. Plan comptable configurable

- Modèle initial configurable.
- Personnalisation par entreprise.
- Comptes locaux.
- Comptes groupe.
- Import.
- Export.
- Historique.
- Interdiction de supprimer un compte utilisé.

Le plan comptable par défaut ne doit pas imposer une norme nationale unique. NEXORA doit permettre de configurer le référentiel adapté au pays et à l'entreprise.

## 6. Hiérarchie comptes

```
1 ACTIFS
  1.1 Actifs courants
  1.2 Actifs non courants

2 PASSIFS
  2.1 Dettes
  2.2 Financements

3 CAPITAUX PROPRES

4 PRODUITS

5 CHARGES
```

Cette structure est illustrative. Le référentiel réel doit être configurable.

## 7. Comptes auxiliaires

- Clients.
- Fournisseurs.
- Banques.
- Caisses.
- Employés si nécessaire.
- Autres tiers.
- Solde par tiers.
- Rapprochement.

## 8. Journaux

- Ventes.
- Achats.
- Banques.
- Caisses.
- Opérations diverses.
- Paie si module ultérieur.
- Stock.
- À-nouveaux.
- Clôture.
- Intercompany.

## 9. Écriture comptable

```
ÉCRITURE
├── date
├── journal
├── numéro
├── devise
├── taux
├── libellé
├── source
└── lignes
     ├── compte
     ├── débit
     ├── crédit
     └── analytique
```

## 10. Équilibre

```
TOTAL DÉBITS = TOTAL CRÉDITS
```

- Contrôle obligatoire à la validation.
- Précision décimale maîtrisée.
- Aucune écriture déséquilibrée ne peut être postée.

## 11. Brouillon

- Création.
- Modification.
- Validation.
- Suppression du brouillon selon permission.
- Historique auteur.

## 12. Écriture validée

- Numéro définitif.
- Date de comptabilisation.
- Journal.
- Période.
- Auteur.
- Validateur.
- Source.
- Hash/empreinte optionnelle selon architecture.
- Audit.

## 13. Contrepassation

- Écriture source.
- Motif.
- Date.
- Écriture inverse.
- Référence croisée.
- Approbation si nécessaire.
- Audit.

## 14. Correction

- Correction par contrepassation.
- Nouvelle écriture.
- Motif.
- Utilisateur.
- Approbation selon seuil.
- Lien entre écritures.

## 15. Périodes comptables

- Ouverte.
- En cours de clôture.
- Clôturée.
- Verrouillée.
- Réouverture exceptionnelle.
- Dates début/fin.
- Entreprise.

## 16. Exercices

- Date début.
- Date fin.
- Statut.
- Entreprise.
- Exercice courant.
- À-nouveaux.
- Clôture.

## 17. Clôture mensuelle

```
CONTRÔLES
 ↓
RAPPROCHEMENTS
 ↓
ÉCRITURES EN ATTENTE
 ↓
BALANCE
 ↓
VALIDATION
 ↓
PÉRIODE CLÔTURÉE
```

- Banques.
- Caisses.
- Clients.
- Fournisseurs.
- Stocks.
- Taxes.
- Suspense.
- Écritures non validées.

## 18. Clôture annuelle

- Résultat.
- Comptes temporaires.
- Report selon référentiel.
- À-nouveaux.
- Validation.
- Verrouillage.
- Audit.

## 19. À-nouveaux

- Génération contrôlée.
- Soldes bilan.
- Clients/fournisseurs selon besoin.
- Banques/caisses.
- Références exercice précédent.
- Audit.

## 20. Comptabilisation des ventes

```
VENTE
 ↓
PRODUIT / SERVICE
 ↓
TAXE
 ↓
CLIENT ou CAISSE/BANQUE
 ↓
STOCK / COÛT DES VENTES SELON RÈGLES
```

- Mapping par catégorie.
- Mapping taxe.
- Mapping paiement.
- Mapping stock.
- Source POS.

## 21. Comptabilisation des achats

```
ACHAT
 ↓
STOCK / CHARGE
 ↓
TAXE
 ↓
FOURNISSEUR ou BANQUE/CAISSE
```

## 22. Créances

- Facture client → compte client.
- Paiement → banque/caisse + client.
- Avoir → correction appropriée.
- Radiation → comptes définis par politique.

## 23. Dettes

- Facture fournisseur → fournisseur.
- Paiement → banque/caisse + fournisseur.
- Avoir fournisseur → réduction dette.
- Acompte → compte approprié.

## 24. Stocks

- Entrée stock.
- Sortie stock.
- Ajustement.
- Coût des ventes.
- Valorisation.
- Inventaire.
- Écarts.
- Mapping comptable.

## 25. Taxes

- Taxe collectée.
- Taxe récupérable.
- Taxe non récupérable.
- Exonération.
- Déclaration selon contexte local.
- Période.
- Compte taxe.

NEXORA doit permettre de paramétrer les obligations fiscales applicables sans prétendre remplacer un conseil fiscal local.

## 26. Immobilisations

- Actif.
- Date acquisition.
- Coût.
- Mise en service.
- Durée.
- Méthode amortissement.
- Amortissement.
- Cession.
- Dépréciation selon modèle.
- Compte associé.

## 27. Amortissements

```
BASE AMORTISSABLE → CALENDRIER → DOTATION → CUMUL → VALEUR NETTE
```

- Mensuel.
- Annuel.
- Prorata selon méthode.
- Révision contrôlée.
- Historique.

## 28. Charges constatées d'avance / produits constatés d'avance

- Période.
- Montant.
- Calendrier.
- Écriture automatique.
- Extourne.
- Audit.

## 29. Provisions

- Type.
- Base.
- Montant.
- Période.
- Compte.
- Validation.
- Reprise.
- Audit.

## 30. Comptabilité analytique

- Centre de coût.
- Projet.
- Département.
- Activité.
- Produit.
- Site.
- Axe personnalisé.
- Répartition.
- Budget vs réalisé.

## 31. Axes analytiques

```
ÉCRITURE
 ↓
COMPTE
 + CENTRE
 + PROJET
 + ACTIVITÉ
 + ÉTABLISSEMENT
```

## 32. Répartition analytique

- Pourcentage.
- Montant.
- Règle.
- Validation 100%.
- Historique.

## 33. Multi-entreprises

```
GROUPE
 ├── ENTREPRISE A → LEDGER A
 ├── ENTREPRISE B → LEDGER B
 └── ENTREPRISE C → LEDGER C
          ↓
       CONSOLIDATION
```

- Grand livre séparé.
- Plan comptable local.
- Reporting groupe.
- Consolidation selon règles.
- Isolation stricte des écritures sources.

## 34. Intercompany

- Compte partenaire.
- Transaction source.
- Montant.
- Devise.
- Référence miroir.
- Élimination en consolidation.
- Écart.
- Rapprochement.

## 35. Consolidation

```
LEDGER A
 + LEDGER B
 + LEDGER C
      ↓
AJUSTEMENTS
      ↓
ÉLIMINATIONS
      ↓
ÉTATS CONSOLIDÉS
```

- Périmètre de consolidation.
- Méthode configurable.
- Éliminations intercompany.
- Ajustements.
- Devise groupe.
- Audit des éliminations.

## 36. Multi-devises

- Devise transaction.
- Devise fonctionnelle.
- Devise groupe.
- Taux transaction.
- Taux clôture.
- Écarts de change.
- Réévaluation.
- Historique.

## 37. Écart de change

```
MONTANT ORIGINAL → TAUX HISTORIQUE / TAUX CLOTURE → ÉCART → ÉCRITURE SELON RÈGLES
```

## 38. Grand livre

- Compte.
- Période.
- Débit.
- Crédit.
- Solde.
- Tiers.
- Journal.
- Source.
- Analytique.
- Export.

## 39. Balance

- Balance générale.
- Balance auxiliaire.
- Débit.
- Crédit.
- Solde.
- Comparaison périodes.
- Par entreprise.
- Consolidée selon scope.

## 40. Compte de résultat

- Produits.
- Charges.
- Résultat brut lorsque pertinent.
- Résultat opérationnel selon modèle.
- Résultat net.
- Comparatif période.
- Budget vs réel si analytique disponible.

## 41. Bilan

- Actifs.
- Passifs.
- Capitaux propres.
- Comparatif.
- Date de clôture.
- Par entreprise.
- Consolidé.

## 42. Tableau des flux de trésorerie

- Flux opérationnels.
- Investissement.
- Financement.
- Méthode selon référentiel choisi.
- Rapprochement avec trésorerie.

## 43. États personnalisés

- Période.
- Comptes.
- Regroupements.
- Filtres.
- Comparaison.
- Format.
- Enregistrement modèle.
- Permissions.

## 44. Reporting fiscal

- Journaux taxes.
- Bases.
- Taxes.
- Périodes.
- Exports.
- Contrôles.
- Paramétrage selon pays.

## 45. Rapprochements

- Banque.
- Caisse.
- Clients.
- Fournisseurs.
- Stock.
- Intercompany.
- Taxes.
- Suspense.

## 46. Comptes de suspense

- Origine.
- Montant.
- Date.
- Responsable.
- Échéance.
- Résolution.
- Écriture finale.
- Audit.

## 47. Contrôles comptables

- Équilibre débit/crédit.
- Période ouverte.
- Compte actif.
- Devise valide.
- Source valide.
- Taxe cohérente.
- Tiers cohérent.
- Intercompany équilibré.
- Séquence document.
- Doublon.

## 48. Audit

- Création.
- Modification brouillon.
- Validation.
- Contrepassation.
- Réouverture période.
- Clôture.
- Changement mapping.
- Changement plan comptable.
- Consolidation.
- Élimination.
- Écart change.

## 49. Permissions

- Lire comptabilité.
- Créer brouillon.
- Poster.
- Valider.
- Contrepasser.
- Clôturer.
- Réouvrir.
- Modifier plan comptable.
- Modifier mapping.
- Voir coûts.
- Voir consolidation.
- Gérer axes analytiques.

## 50. Séparation des responsabilités

- Préparer écriture.
- Valider.
- Clôturer.
- Réouvrir.
- Modifier mapping.
- Publier consolidation.
- Auditer.

## 51. API principales

```
GET/POST /api/v1/chart-of-accounts
GET/POST /api/v1/accounts
GET/POST /api/v1/journals
GET/POST /api/v1/accounting-entries
POST     /api/v1/accounting-entries/{id}/post
POST     /api/v1/accounting-entries/{id}/reverse
GET      /api/v1/general-ledger
GET      /api/v1/trial-balance
GET      /api/v1/financial-statements
GET      /api/v1/analytical-reports
POST     /api/v1/accounting-periods/{id}/close
POST     /api/v1/accounting-periods/{id}/reopen
GET      /api/v1/consolidation
POST     /api/v1/consolidation/{id}/run
```

## 52. Événements métier

```
JournalEntryDrafted
JournalEntryPosted
JournalEntryReversed
AccountingPeriodClosed
AccountingPeriodReopened
AssetDepreciated
IntercompanyMatched
ConsolidationRun
ConsolidationAdjusted
```

## 53. Tests fondamentaux

- Écriture équilibrée.
- Écriture déséquilibrée refusée.
- Période fermée refusée.
- Contrepassation.
- Vente comptant.
- Vente crédit.
- Achat comptant.
- Achat crédit.
- Paiement client.
- Paiement fournisseur.
- Stock.
- Taxe.
- Amortissement.
- CCA/PCA.
- Provision.
- Analytique.
- Multi-devise.
- Intercompany.
- Consolidation.
- À-nouveaux.

## 54. Tests d'intégration

- Vente POS → journal.
- Achat → journal.
- Paiement → journal.
- Retour → journal.
- Stock → coût → journal.
- Banque → rapprochement → journal.
- Mobile Money → journal.
- Créance → paiement → solde.
- Dette → paiement → solde.
- Clôture finance → comptabilité.

## 55. Tests d'isolation

- Entreprise A ne voit pas ledger B.
- Comptable A ne poste pas dans B.
- Comptable en chef voit uniquement les entreprises autorisées.
- Consolidation ne modifie pas les ledgers sources.
- Intercompany conserve les traces des deux côtés.

## 56. Scénario de validation

```
VENTE 100 000
→ CRÉANCE 100 000
→ PAIEMENT 60 000
→ SOLDE CLIENT 40 000

ACHAT 50 000 À CRÉDIT
→ DETTE 50 000

STOCK
→ ENTRÉE 50 000
→ SORTIE COÛT SELON VENTE

FINANCE
→ BANQUE +60 000

COMPTABILITÉ
→ TOUS LES JOURNAUX ÉQUILIBRÉS
```

## 57. Critères de sortie

- Plan comptable.
- Journaux.
- Écritures.
- Débit/crédit.
- Brouillons.
- Validation.
- Contrepassation.
- Périodes.
- Exercices.
- À-nouveaux.
- Ventes.
- Achats.
- Créances.
- Dettes.
- Stock.
- Taxes.
- Immobilisations.
- CCA/PCA.
- Provisions.
- Analytique.
- Multi-entreprises.
- Intercompany.
- Consolidation.
- Multi-devises.
- Grand livre.
- Balance.
- Bilan.
- Résultat.
- Flux de trésorerie.
- Rapprochements.
- Contrôles.
- Permissions.
- Audit.
- API.
- Tests.

## 58. Étape suivante

Passer à la Phase 30 — Facturation, documents commerciaux, devis, commandes, factures, avoirs, reçus, signatures, numérotation, PDF et communication client. Elle renforcera la couche documentaire de NEXORA autour des ventes et achats.

---

**NEXORA V1.0 — PHASE 29 VALIDÉE → PHASE 30 : FACTURATION & DOCUMENTS**
