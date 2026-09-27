# NEXORA ONE — PHASE 39 — MIGRATION, IMPORTATION & ONBOARDING AVANCÉ

*Migration depuis Excel, anciens logiciels et systèmes externes — NEXORA ONE V1.0*

**Version 1.0**

## 1. Objectif

Permettre à une entreprise de commencer avec NEXORA ONE sans repartir de zéro. Le système doit importer, nettoyer, mapper, valider et intégrer les données provenant d'Excel, CSV, anciens logiciels, bases de données et connecteurs, avec prévisualisation, contrôles, journalisation et possibilité de correction.

## 2. Principe

```
SOURCE
 ↓
IMPORT
 ↓
PROFILAGE
 ↓
MAPPING
 ↓
VALIDATION
 ↓
PRÉVISUALISATION
 ↓
IMPORT TEST
 ↓
VÉRIFICATION
 ↓
IMPORT FINAL
 ↓
RAPPROCHEMENT
```

- Ne jamais importer directement des données inconnues en production sans validation.
- Conserver le fichier/source original lorsque la politique le permet.
- Chaque import possède un identifiant, un auteur et un historique.
- Les erreurs doivent être localisables ligne par ligne.

## 3. Sources

- Excel.
- CSV.
- JSON.
- XML lorsque nécessaire.
- Ancienne base SQL via connecteur.
- API externe.
- Autre ERP/POS.
- Fichiers bancaires.
- Exports comptables.
- Import manuel.

## 4. Données importables

- Entreprises.
- Établissements.
- Utilisateurs selon politique.
- Clients.
- Fournisseurs.
- Produits.
- Catégories.
- Prix.
- Stocks.
- Lots.
- Expiration.
- Commandes.
- Factures historiques.
- Paiements.
- Créances.
- Dettes.
- Comptes comptables.
- Soldes d'ouverture.
- Immobilisations.
- Budgets.
- Documents.
- Configuration.

## 5. Assistant migration

```
1 SOURCE
2 FICHIER / CONNECTEUR
3 PROFILAGE
4 MAPPING
5 VALIDATION
6 SIMULATION
7 IMPORT
8 CONTRÔLE
9 FINALISATION
```

## 6. Profilage

- Nombre lignes.
- Colonnes.
- Types détectés.
- Valeurs manquantes.
- Doublons.
- Formats invalides.
- Valeurs uniques.
- Dates.
- Devise.
- Montants.
- Références.

## 7. Mapping

- Colonne source → champ NEXORA ONE.
- Mapping automatique suggéré.
- Mapping manuel.
- Mapping sauvegardé.
- Transformations.
- Valeur par défaut.
- Conversion unité.
- Conversion devise.
- Normalisation.

## 8. Transformation

- Trim espaces.
- Normalisation téléphone.
- Email.
- Dates.
- Décimales.
- Séparateurs.
- Unités.
- Codes produits.
- Catégories.
- Taxes.
- Statuts.
- Devise.

## 9. Correspondance entités

```
SOURCE CLIENT
      ↓
MATCH
 ├── EXACT
 ├── PROBABLE
 └── NO MATCH
      ↓
VALIDATION
```

- Identifiant externe.
- Email.
- Téléphone.
- Nom.
- Code.
- Référence.

## 10. Doublons

- Détection.
- Score de similarité lorsque pertinent.
- Règle métier.
- Fusion proposée.
- Fusion manuelle.
- Conservation historique.
- Journal.

## 11. Produits

- SKU.
- Code-barres.
- Nom.
- Catégorie.
- Prix.
- Coût.
- Unité.
- Taxe.
- Stock.
- Lot.
- Expiration.
- Fournisseur.
- Produit pesé.

## 12. Clients

- Code.
- Nom.
- Téléphone.
- Email.
- Adresse.
- Conditions crédit.
- Limite.
- Solde initial.
- Commercial.
- Catégorie.

## 13. Fournisseurs

- Code.
- Nom.
- Contacts.
- Conditions paiement.
- Solde initial.
- Devise.
- Catégorie.
- Référence externe.

## 14. Stocks

- Entrepôt.
- Emplacement.
- Produit.
- Quantité.
- Coût.
- Lot.
- Expiration.
- Numéro série.
- Date inventaire.
- Stock disponible.

Les stocks importés doivent être traités comme un état initial documenté, avec une écriture ou un événement d'ouverture permettant de conserver la traçabilité.

## 15. Historique ventes

- Date.
- Document.
- Client.
- Produits.
- Quantités.
- Prix.
- Taxes.
- Remises.
- Total.
- Paiement.
- Statut.
- Référence externe.

## 16. Historique achats

- Fournisseur.
- Document.
- Date.
- Produits.
- Quantités.
- Coûts.
- Taxes.
- Total.
- Paiement.
- Statut.

## 17. Créances

- Client.
- Document.
- Date.
- Montant.
- Échéance.
- Payé.
- Solde.
- Ancienneté.
- Référence.

## 18. Dettes

- Fournisseur.
- Document.
- Date.
- Montant.
- Échéance.
- Payé.
- Solde.
- Ancienneté.
- Référence.

## 19. Comptabilité

- Plan comptable.
- Comptes.
- Journaux.
- Soldes ouverture.
- Écritures historiques.
- Tiers.
- Taxes.
- Axes analytiques.
- Devise.
- Exercice.

## 20. Migration comptable

```
ANCIEN PLAN
 ↓
TABLE DE CORRESPONDANCE
 ↓
PLAN NEXORA ONE
 ↓
BALANCE OUVERTURE
 ↓
CONTRÔLE DÉBIT = CRÉDIT
```

- Mapping compte par compte.
- Contrôle équilibre.
- Période d'ouverture.
- Pièces justificatives lorsque disponibles.
- Validation comptable.

## 21. Migration financière

- Caisses.
- Banques.
- Mobile Money.
- Soldes.
- Transactions d'ouverture.
- Budgets.
- Catégories.
- Créances/dettes.
- Rapprochements à reprendre lorsque possible.

## 22. Documents

- Factures.
- Reçus.
- Contrats.
- Pièces justificatives.
- Images.
- PDF.
- Métadonnées.
- Références externes.
- Hash lorsque pertinent.

## 23. Import test

- Sandbox.
- Échantillon.
- Résultats.
- Erreurs.
- Warnings.
- Temps estimé.
- Impact estimé.
- Aucune modification production.

## 24. Prévisualisation

```
IMPORT
 ↓
N lignes OK
N warnings
N erreurs
N doublons
N créations
N mises à jour
N rejets
 ↓
VALIDER / ANNULER
```

## 25. Validation

- Champs obligatoires.
- Références.
- Types.
- Dates.
- Montants.
- Devises.
- Taxes.
- Unités.
- Permissions.
- Contraintes métier.
- Intégrité.

## 26. Gestion erreurs

- Erreur bloquante.
- Warning.
- Ligne rejetée.
- Correction inline lorsque possible.
- Export erreurs.
- Réimport des lignes corrigées.
- Historique.

## 27. Import par lots

- Batch.
- Taille configurable.
- Progression.
- Pause.
- Reprise.
- Retry.
- Journal.
- Contrôle mémoire.

## 28. Idempotence

- Identifiant import.
- Identifiant source.
- Empêcher doublon.
- Mode création.
- Mode mise à jour.
- Mode upsert.
- Mode rejet doublon.

## 29. Modes import

- Créer uniquement.
- Mettre à jour uniquement.
- Créer ou mettre à jour.
- Ignorer doublons.
- Rejeter doublons.
- Synchronisation continue pour connecteurs.

## 30. Import initial multi-entreprises

- Plusieurs entreprises.
- Mapping par entreprise.
- Catégories locales.
- Plans comptables différents.
- Devises différentes.
- Entrepôts différents.
- Rôles différents.
- Validation scope.

## 31. Onboarding automatique

```
TYPE ENTREPRISE
 ↓
PROFIL SECTEUR
 ↓
CONFIGURATION DE BASE
 ↓
IMPORT
 ↓
UTILISATEURS
 ↓
PREMIÈRES DONNÉES
 ↓
CHECKLIST
 ↓
ACTIVATION
```

## 32. Checklist onboarding

- Entreprise.
- Logo.
- Devise.
- Taxes.
- Établissements.
- Entrepôts.
- Caisses.
- Utilisateurs.
- Rôles.
- Produits.
- Clients.
- Fournisseurs.
- Stock initial.
- Comptes.
- Paiements.
- Documents.
- Notifications.
- Backup.
- Tests.

## 33. Assistant de migration intelligent

- Détection du secteur.
- Suggestion mapping.
- Détection anomalies.
- Suggestion catégories.
- Détection devise.
- Suggestion comptes.
- Alerte données manquantes.
- Score préparation.

## 34. Score de migration

```
DONNÉES
████████░░ 80%
MAPPING
█████████░ 90%
QUALITÉ
███████░░░ 70%
COMPTABILITÉ
████████░░ 80%
PRÊT = NON
```

Le score est indicatif et ne remplace pas la validation métier.

## 35. Rapprochement post-import

- Nombre lignes source vs NEXORA ONE.
- Total stock.
- Total créances.
- Total dettes.
- Balance.
- CA historique.
- Paiements.
- Documents.
- Écarts.
- Validation.

## 36. Audit migration

- Source.
- Fichier.
- Utilisateur.
- Date.
- Mapping.
- Version.
- Nombre lignes.
- Erreurs.
- Créations.
- Mises à jour.
- Rejets.
- Validation.
- Finalisation.

## 37. Rollback

- Annulation avant finalisation.
- Suppression des objets créés par un import identifiable lorsque sûr.
- Transaction par lot.
- Procédure corrective pour données déjà liées.
- Journal complet.

## 38. Migration sans interruption

- Import parallèle.
- Gel minimal.
- Delta final.
- Double lecture lorsque possible.
- Bascule contrôlée.
- Vérification.

## 39. Migration locale

- Fichiers locaux.
- Ancienne base locale.
- Import sur serveur local.
- Backup avant import.
- Validation offline.

## 40. Migration cloud

- Upload sécurisé.
- Stockage temporaire chiffré.
- Import asynchrone.
- Suppression fichier temporaire selon politique.
- Journal.

## 41. Migration hybride

- Extraction locale.
- Transfert sécurisé.
- Import central.
- Delta local.
- Synchronisation.
- Contrôle conflits.

## 42. Sécurité

- Fichiers chiffrés en transit.
- Accès temporaire.
- Scan fichiers.
- Secrets exclus.
- Données limitées.
- Suppression contrôlée.
- Audit.
- Scope entreprise.

## 43. Confidentialité

- Ne pas envoyer les données vers un service externe pour analyse sans autorisation.
- Masquage lorsque nécessaire.
- Accès migration limité aux personnes autorisées.

## 44. Formats standards

- CSV UTF-8.
- XLSX.
- JSON.
- PDF pour documents, avec extraction contrôlée lorsque nécessaire.
- Formats bancaires selon fournisseur.
- Formats comptables selon système source.

## 45. Import API

```
POST /api/v1/imports
GET  /api/v1/imports/{id}
POST /api/v1/imports/{id}/profile
POST /api/v1/imports/{id}/mapping
POST /api/v1/imports/{id}/validate
POST /api/v1/imports/{id}/simulate
POST /api/v1/imports/{id}/execute
POST /api/v1/imports/{id}/pause
POST /api/v1/imports/{id}/resume
POST /api/v1/imports/{id}/cancel
GET  /api/v1/imports/{id}/errors
GET  /api/v1/imports/{id}/reconciliation
GET  /api/v1/onboarding/checklist
```

## 46. Événements

```
ImportCreated
ImportProfiled
ImportMapped
ImportValidated
ImportSimulationCompleted
ImportStarted
ImportProgressed
ImportPaused
ImportCompleted
ImportFailed
ImportCancelled
MigrationReconciled
OnboardingStepCompleted
```

## 47. Tests

- CSV.
- Excel.
- API.
- Ancienne DB.
- Clients.
- Fournisseurs.
- Produits.
- Stock.
- Lots.
- Expiration.
- Ventes.
- Achats.
- Créances.
- Dettes.
- Comptabilité.
- Finance.
- Documents.
- Doublons.
- Mapping.
- Transformation.
- Validation.
- Simulation.
- Batch.
- Retry.
- Idempotence.
- Rollback.
- Multi-entreprises.
- Local.
- Cloud.
- Hybride.
- Sécurité.
- Rapprochement.

## 48. Scénario complet

```
ANCIEN LOGICIEL
      ↓
EXPORT CLIENTS / PRODUITS / STOCK
      ↓
NEXORA ONE PROFILAGE
      ↓
MAPPING
      ↓
SIMULATION
      ↓
CORRECTION
      ↓
IMPORT
      ↓
RAPPROCHEMENT
      ↓
VALIDATION
      ↓
NEXORA ONE ACTIF
```

## 49. Critères de sortie

- Sources.
- Données importables.
- Assistant.
- Profilage.
- Mapping.
- Transformation.
- Correspondance.
- Doublons.
- Produits.
- Clients.
- Fournisseurs.
- Stocks.
- Ventes.
- Achats.
- Créances.
- Dettes.
- Comptabilité.
- Finance.
- Documents.
- Import test.
- Prévisualisation.
- Validation.
- Erreurs.
- Batch.
- Idempotence.
- Modes import.
- Multi-entreprises.
- Onboarding.
- Score.
- Rapprochement.
- Audit.
- Rollback.
- Migration sans interruption.
- Local/cloud/hybride.
- Sécurité.
- Confidentialité.
- Formats.
- API.
- Événements.
- Tests.

## 50. Étape suivante

Passer à la Phase 40 — Expérience utilisateur, design system, accessibilité et personnalisation de l'interface. Elle consolidera l'identité premium de NEXORA ONE tout en garantissant une interface simple pour les petites entreprises et puissante pour les groupes.

---

**NEXORA ONE V1.0 — PHASE 39 VALIDÉE → PHASE 40 : UX, DESIGN SYSTEM & ACCESSIBILITÉ**
