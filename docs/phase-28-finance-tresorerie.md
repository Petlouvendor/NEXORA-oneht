# NEXORA — PHASE 28 — FINANCE & TRÉSORERIE

**Moteur financier et trésorerie multi-entreprises NEXORA V1.0**
**Version 1.0**

## 1. Objectif

Centraliser les flux monétaires de NEXORA : caisses, banques, Mobile Money, encaissements, décaissements, dépenses, transferts, créances, dettes, budgets, prévisions et rapprochements. La finance doit fournir une vision opérationnelle fiable sans remplacer le moteur comptable.

## 2. Principe

```
VENTES ───────→ ENCAISSEMENTS
ACHATS ───────→ DÉCAISSEMENTS
CRÉANCES ─────→ ENCAISSEMENTS
DETTES ───────→ DÉCAISSEMENTS
DÉPENSES ─────→ DÉCAISSEMENTS
TRANSFERTS ───→ DÉPLACEMENTS
                     ↓
              TRÉSORERIE
                     ↓
              COMPTABILITÉ
```

- La finance suit les mouvements de fonds.
- La comptabilité reste le système de référence pour les états financiers.
- Chaque mouvement doit avoir une source, un compte financier et un statut.
- Les soldes doivent être réconciliables.

## 3. Comptes financiers

- Caisse.
- Banque.
- Compte Mobile Money.
- Portefeuille électronique lorsque pertinent.
- Compte de transit.
- Compte de suspense.
- Compte de paiement externe.
- Compte de dépôt.

## 4. Fiche compte financier

- Nom.
- Code.
- Type.
- Entreprise.
- Établissement.
- Devise.
- Institution.
- Numéro masqué.
- Solde système.
- Statut.
- Date ouverture.
- Date fermeture.
- Compte comptable associé.

## 5. Caisses

- Caisse physique.
- Caisse POS.
- Caisse centrale.
- Caisse petite dépense.
- Session.
- Utilisateur responsable.
- Devise.
- Fond initial.
- Statut.

## 6. Sessions caisse

```
OUVERTURE
 ↓
FOND INITIAL
 ↓
ENCAISSEMENTS / DÉCAISSEMENTS
 ↓
COMPTAGE
 ↓
ÉCART
 ↓
CLÔTURE
```

- Une session appartient à une caisse.
- Les opérations sont horodatées.
- Clôture contrôlée.
- Réouverture exceptionnelle auditée.

## 7. Mouvements de caisse

- Vente.
- Paiement client.
- Remboursement.
- Achat comptant.
- Dépense.
- Retrait.
- Dépôt.
- Transfert.
- Correction contrôlée.

## 8. Fond de caisse

- Montant initial.
- Devise.
- Utilisateur.
- Date.
- Source.
- Comptage.
- Historique.

## 9. Écarts de caisse

- Attendu.
- Compté.
- Écart.
- Motif.
- Responsable.
- Approbation selon seuil.
- Écriture/correction selon règles.
- Audit.

## 10. Banques

- Compte bancaire.
- Banque.
- Agence optionnelle.
- Devise.
- Titulaire.
- Statut.
- Solde système.
- Dernier rapprochement.
- Date dernier relevé.

## 11. Relevés bancaires

- Import manuel.
- CSV/format structuré.
- API bancaire lorsque disponible.
- Date.
- Référence.
- Libellé.
- Débit.
- Crédit.
- Solde.
- Compte.

## 12. Rapprochement bancaire

```
RELEVÉ BANCAIRE
       ↕
TRANSACTIONS NEXORA
       ↓
MATCH
 / AUTO-MATCH /
 / MANUEL /
 / SUSPENSE
       ↓
RÉCONCILIÉ
```

- Correspondance référence.
- Montant.
- Date.
- Tolérance.
- Règles.
- Propositions.
- Validation.
- Écarts.

## 13. Mobile Money

- Compte opérateur.
- Devise.
- Encaissement.
- Décaissement.
- Frais.
- Référence.
- Statut.
- Rapprochement.
- API si disponible.

Les fournisseurs et API disponibles dépendent du marché. Le système doit utiliser une couche d'intégration remplaçable.

## 14. Paiements externes

- Transaction.
- Provider.
- Reference.
- Montant.
- Devise.
- Statut.
- Frais.
- Date.
- Confirmation.
- Remboursement.
- Webhook.
- Idempotence.

## 15. Frais financiers

- Frais carte.
- Frais Mobile Money.
- Frais bancaire.
- Commissions.
- Autres frais.
- Compte financier.
- Compte comptable.
- Allocation à transaction.

## 16. Dépenses

- Catégorie.
- Fournisseur/bénéficiaire.
- Montant.
- Taxe.
- Date.
- Entreprise.
- Établissement.
- Compte financier.
- Pièce justificative.
- Budget.
- Approbation.
- Statut.

## 17. Petite caisse

- Demande.
- Dépense.
- Plafond.
- Justificatif.
- Responsable.
- Réapprovisionnement.
- Comptage.
- Audit.

## 18. Transferts financiers

```
CAISSE A
   ↓
TRANSFERT
   ↓
EN TRANSIT
   ↓
BANQUE B
   ↓
RÉCEPTION
```

- Source.
- Destination.
- Montant.
- Devise.
- Frais.
- Référence.
- Date.
- Statut.
- Confirmation.
- Audit.

## 19. Transferts inter-entreprises

- Entreprise source.
- Entreprise destination.
- Motif.
- Montant.
- Devise.
- Approbation.
- Compte source.
- Compte destination.
- Écriture intercompany.
- Rapprochement.
- Audit.

## 20. Créances

- Solde clients.
- Encaissements.
- Aging.
- Échéances.
- Prévisions.
- Retards.
- Relances.
- Impact trésorerie.

## 21. Dettes

- Solde fournisseurs.
- Paiements prévus.
- Échéances.
- Retards.
- Priorités.
- Impact trésorerie.
- Calendrier.

## 22. Prévision de trésorerie

```
SOLDE INITIAL
+ ENTRÉES PRÉVUES
- SORTIES PRÉVUES
= SOLDE PRÉVISIONNEL
```

- Horizon configurable.
- Scénario prudent.
- Scénario central.
- Scénario optimiste.
- Encaissements attendus.
- Paiements fournisseurs.
- Salaires/dépenses si intégrés.
- Taxes.
- Financements.
- Investissements.

## 23. Prévision par maturité

- Aujourd'hui.
- 7 jours.
- 30 jours.
- 60 jours.
- 90 jours.
- 12 mois selon module.

## 24. Budgets

- Budget annuel.
- Mensuel.
- Par entreprise.
- Par établissement.
- Par département.
- Par catégorie.
- Révisé.
- Réalisé.
- Engagé.
- Disponible.
- Écart.

## 25. Contrôle budget

```
BUDGET → ENGAGÉ → DÉPENSE/FACTURE → RÉALISÉ → VARIANCE
```

- Alerte dépassement.
- Blocage optionnel.
- Approbation exceptionnelle.
- Historique révisions.

## 26. Analyse flux de trésorerie

- Entrées.
- Sorties.
- Net.
- Par compte.
- Par entreprise.
- Par établissement.
- Par catégorie.
- Par période.
- Tendance.

## 27. Position de trésorerie

- Cash disponible.
- Banques.
- Caisses.
- Mobile Money.
- Transits.
- Fonds bloqués.
- Créances proches.
- Dettes proches.

## 28. Liquidité

- Liquidité immédiate.
- Engagements court terme.
- Créances attendues.
- Dettes attendues.
- Gap de trésorerie.
- Alertes.

## 29. Multi-devises

- Compte par devise.
- Solde original.
- Solde fonctionnel.
- Taux.
- Date taux.
- Gain/perte de change.
- Réévaluation selon règles comptables.

## 30. Conversion

```
MONTANT ORIGINAL × TAUX = MONTANT FONCTIONNEL
```

- Taux historique pour transaction.
- Taux de clôture pour réévaluation selon règles.
- Historique des taux.
- Source.

## 31. Rapprochement financier

- Caisse.
- Banque.
- Mobile Money.
- Créances.
- Dettes.
- Transferts.
- Paiements externes.
- Écarts.
- Suspense.

## 32. Comptes de suspense

- Transaction non identifiée.
- Paiement sans facture.
- Frais non affectés.
- Écart bancaire.
- Écart Mobile Money.
- Responsable.
- Date limite résolution.
- Résolution auditée.

## 33. Calendrier de trésorerie

- Entrées prévues.
- Sorties prévues.
- Échéances.
- Priorité.
- Statut.
- Responsable.
- Alerte.

## 34. Scénarios

- Scénario normal.
- Retard clients.
- Paiement fournisseur anticipé.
- Baisse ventes.
- Hausse achats.
- Dépense exceptionnelle.
- Simulation.

## 35. Gestion des financements

- Prêt.
- Décaissement.
- Échéancier.
- Principal.
- Intérêts.
- Frais.
- Remboursements.
- Solde.
- Compte financier.
- Comptabilité.

## 36. Actifs financiers simples

- Dépôt.
- Avance.
- Garantie.
- Acompte.
- Autres actifs court terme selon modèle comptable.

## 37. Remboursements

- Client.
- Fournisseur.
- Paiement externe.
- Montant.
- Moyen.
- Compte source.
- Statut.
- Référence.
- Audit.

## 38. Clôture financière

- Période.
- Contrôles.
- Comptes rapprochés.
- Écarts.
- Transactions en suspense.
- Créances/dettes.
- Validation.
- Rapport.
- Audit.

## 39. Intégration ventes

```
VENTE → PAIEMENT → COMPTE FINANCIER → SOLDE → RAPPROCHEMENT → COMPTABILITÉ
```

## 40. Intégration achats

```
ACHAT/FACTURE → PAIEMENT → COMPTE FINANCIER → SOLDE → RAPPROCHEMENT → COMPTABILITÉ
```

## 41. Intégration crédit

- Créance créée à la vente crédit.
- Paiement client augmente compte financier.
- Solde créance diminué.
- Prévision encaissement mise à jour.

## 42. Intégration dettes

- Dette fournisseur créée.
- Paiement diminue dette.
- Sortie de trésorerie.
- Prévision mise à jour.

## 43. Comptabilité

```
MOUVEMENT FINANCIER
       ↓
COMPTE FINANCIER
       ↓
ÉVÉNEMENT COMPTABLE
       ↓
JOURNAL
       ↓
GRAND LIVRE
```

Les écritures sont produites selon les règles comptables et le mapping de l'entreprise.

## 44. Dashboard finance

- Trésorerie totale.
- Solde par compte.
- Entrées/sorties.
- Prévisions.
- Créances.
- Dettes.
- Échéances.
- Écarts.
- Suspense.
- Budget.
- Alertes.

## 45. Dashboard direction groupe

- Trésorerie consolidée selon périmètre.
- Liquidité par entreprise.
- Créances groupe.
- Dettes groupe.
- Flux intercompany.
- Prévisions.
- Comparaison entreprises.
- Alertes majeures.

## 46. Permissions

- Lire finance.
- Créer mouvement.
- Modifier mouvement avant validation.
- Approuver dépense.
- Payer.
- Transférer.
- Importer relevé.
- Rapprocher.
- Gérer budgets.
- Voir coûts.
- Réévaluer devise.
- Clôturer période.

## 47. Séparation des responsabilités

- Création bénéficiaire.
- Création paiement.
- Approbation paiement.
- Exécution paiement.
- Rapprochement.
- Modification compte bancaire.
- Clôture.

## 48. Anti-fraude

- Paiement vers nouveau bénéficiaire.
- Changement coordonnées bancaires.
- Paiements fractionnés.
- Paiements hors horaires selon politique.
- Écarts caisse.
- Remboursements inhabituels.
- Transactions en suspense anciennes.
- Transferts circulaires.
- Dépenses hors budget.

## 49. Audit

- Création compte financier.
- Modification.
- Mouvement.
- Paiement.
- Remboursement.
- Transfert.
- Rapprochement.
- Budget.
- Réévaluation.
- Clôture.
- Correction.
- Approbation.

## 50. API principales

```
GET/POST /api/v1/financial-accounts
GET      /api/v1/cash-sessions
POST     /api/v1/cash-movements
GET      /api/v1/bank-transactions
POST     /api/v1/bank-imports
POST     /api/v1/bank-reconciliations
GET      /api/v1/mobile-money/accounts
GET      /api/v1/financial-transactions
POST     /api/v1/financial-transfers
GET/POST /api/v1/expenses
GET/POST /api/v1/budgets
GET      /api/v1/cashflow/forecast
GET      /api/v1/treasury/position
POST     /api/v1/periods/{id}/financial-close
```

## 51. Événements métier

```
CashOpened
CashMovementCreated
CashClosed
BankTransactionImported
BankTransactionReconciled
FinancialTransferCreated
FinancialTransferConfirmed
ExpenseCreated
ExpenseApproved
CustomerPaymentConfirmed
SupplierPaymentConfirmed
FinancialPeriodClosed
```

## 52. Offline

- Caisse offline supportée.
- Mouvements locaux avec identifiants uniques.
- Synchronisation idempotente.
- Paiements externes non confirmés localement comme réussis.
- Conflits signalés.
- Clôture offline selon politique stricte.

## 53. Performance

- Soldes agrégés efficacement.
- Index par compte/date.
- Imports relevés asynchrones.
- Rapprochement massif asynchrone.
- Prévisions calculées en tâche lorsque lourdes.
- Pagination transactions.

## 54. Tests

- Ouverture caisse.
- Vente encaissée.
- Remboursement.
- Dépense.
- Transfert.
- Import bancaire.
- Match automatique.
- Match manuel.
- Suspense.
- Mobile Money.
- Créance.
- Dette.
- Budget.
- Prévision.
- Multi-devise.
- Écart change.
- Clôture.
- Multi-entreprises.
- Permissions.
- Audit.

## 55. Scénario complet

```
VENTES                 +500 000
ACHATS                 -200 000
DÉPENSES                -50 000
PAIEMENTS CLIENTS      +300 000
PAIEMENTS FOURNISSEURS -100 000
TRANSFERTS             ± interne
                         ↓
              POSITION TRÉSORERIE
                         ↓
                  RAPPROCHEMENT
                         ↓
                   COMPTABILITÉ
```

## 56. Critères de sortie

- Comptes financiers.
- Caisses.
- Sessions.
- Mouvements.
- Banques.
- Import relevés.
- Rapprochement.
- Mobile Money.
- Paiements externes.
- Dépenses.
- Petite caisse.
- Transferts.
- Créances.
- Dettes.
- Prévisions.
- Budgets.
- Liquidité.
- Multi-devises.
- Suspense.
- Clôture.
- Dashboards.
- Permissions.
- Séparation responsabilités.
- Audit.
- Anti-fraude.
- Offline.
- API.
- Tests.

## 57. Étape suivante

Passer à la Phase 29 — Comptabilité générale, journaux, plan comptable, écritures, grand livre, balance, bilan, compte de résultat, clôtures et contrôles. Cette phase transformera les flux commerciaux et financiers de NEXORA en véritable système comptable.

---

**NEXORA V1.0 — PHASE 28 VALIDÉE → PHASE 29 : COMPTABILITÉ GÉNÉRALE**
