# NEXORA — PHASE 30 — FACTURATION & DOCUMENTS COMMERCIAUX

**Moteur documentaire commercial NEXORA V1.0**
**Version 1.0**

## 1. Objectif

Construire un moteur documentaire professionnel couvrant les devis, commandes, factures, avoirs, reçus, bons de livraison, bons de réception, relevés et autres documents commerciaux. Les documents doivent être cohérents avec les transactions, la finance, la comptabilité, les entreprises et les permissions.

## 2. Principe

```
DONNÉES MÉTIER
      ↓
DOCUMENT
      ↓
VALIDATION
      ↓
NUMÉROTATION
      ↓
PDF / IMPRESSION / DIGITAL
      ↓
ARCHIVAGE + AUDIT
```

- Un document validé conserve son contenu historique.
- Les modèles peuvent être personnalisés sans modifier les données sources.
- Les documents doivent être reconstituables.
- La numérotation doit être contrôlée par entreprise et type.

## 3. Types de documents

- Devis.
- Proforma.
- Commande client.
- Facture.
- Facture d'acompte.
- Facture finale.
- Avoir.
- Reçu.
- Bon de livraison.
- Bon de retour.
- Relevé client.
- État de compte.
- Bon de commande fournisseur.
- Bon de réception.
- Facture fournisseur.
- Avoir fournisseur.
- Relevé fournisseur.
- Document interne.

## 4. Devis

- Client.
- Date.
- Validité.
- Lignes.
- Prix.
- Remises.
- Taxes.
- Conditions.
- Notes.
- Statut.
- Version.

## 5. Cycle devis

```
BROUILLON
 ↓
ENVOYÉ
 ↓
CONSULTÉ
 ↓
ACCEPTÉ / REFUSÉ / EXPIRÉ
 ↓
COMMANDE
```

## 6. Proforma

- Document non comptable selon configuration.
- Client.
- Lignes.
- Taxes.
- Conditions.
- Validité.
- Mention explicite du statut.

## 7. Commande client

- Client.
- Référence.
- Lignes.
- Prix snapshot.
- Taxes.
- Remises.
- Acompte.
- Crédit.
- Livraison.
- Réservation stock.
- Statut.

## 8. Facture

- Numéro.
- Entreprise émettrice.
- Client.
- Date émission.
- Date échéance.
- Lignes.
- Quantités.
- Prix.
- Remises.
- Taxes.
- Total HT.
- Total taxe.
- Total TTC.
- Montant payé.
- Solde.
- Devise.
- Conditions.
- Références.

## 9. Facture à crédit

```
FACTURE
 ↓
TOTAL
 ↓
PAIEMENT PARTIEL / 0
 ↓
CRÉANCE
 ↓
ÉCHÉANCE
 ↓
RELEVÉ / RELANCE
```

## 10. Facture d'acompte

- Commande source.
- Montant/taux.
- Taxe selon règles.
- Acompte reçu.
- Solde commande.
- Imputation facture finale.

## 11. Facture finale

- Références acomptes.
- Montant brut.
- Acomptes déduits.
- Solde.
- Taxes.
- Paiements.
- Créance résiduelle.

## 12. Avoir

- Facture source.
- Motif.
- Lignes.
- Quantité.
- Montant.
- Taxes.
- Remboursement ou crédit.
- Numéro.
- Audit.

## 13. Reçu

- Paiement source.
- Client/fournisseur selon contexte.
- Montant.
- Moyen.
- Date.
- Référence.
- Solde après paiement.
- Signature optionnelle.
- QR optionnel.

## 14. Bon de livraison

- Commande source.
- Client.
- Adresse.
- Lignes.
- Quantités.
- Lots.
- Transporteur.
- Date.
- Preuve livraison.
- Signature optionnelle.

## 15. Bon de réception

- Commande fournisseur.
- Fournisseur.
- Quantités reçues.
- Lots.
- Expiration.
- Écarts.
- Date.
- Responsable.
- Emplacement.

## 16. Documents fournisseurs

- Commande fournisseur.
- Proforma fournisseur.
- Bon réception.
- Facture fournisseur.
- Avoir fournisseur.
- Relevé fournisseur.
- Justificatif paiement.

## 17. Relevé client

```
SOLDE INITIAL
+ FACTURES
- PAIEMENTS
- AVOIRS
= SOLDE FINAL
```

- Période.
- Liste transactions.
- Échéances.
- Solde.
- Devise.
- PDF.
- Export.

## 18. État de compte fournisseur

- Factures.
- Avoirs.
- Paiements.
- Solde.
- Échéances.
- Retards.
- Période.

## 19. Numérotation

- Séquence par entreprise.
- Type document.
- Préfixe.
- Année/période optionnelle.
- Séquence sans doublon.
- Numéro définitif à l'émission/validation selon politique.
- Annulation contrôlée.
- Traçabilité des trous de séquence.

## 20. Séries

```
INV-2026-000001
INV-2026-000002
...
CRN-2026-000001
RCV-2026-000001
```

- Séries par établissement si nécessaire.
- Séries par type.
- Configuration par entreprise.
- Période de réinitialisation selon règles.

## 21. Modèles de documents

- Logo.
- Nom légal.
- Adresse.
- Téléphone.
- Email.
- Identifiants fiscaux selon pays.
- Couleurs.
- Polices.
- Colonnes.
- Pied de page.
- Conditions.
- Signature.
- QR code.
- Mentions légales.

## 22. Multi-entreprises

- Modèle par groupe.
- Modèle par entreprise.
- Modèle par établissement.
- Numérotation indépendante.
- Coordonnées propres.
- Devise.
- Langue.
- Mentions légales propres.

## 23. Multi-langues

- Français.
- Anglais.
- Autres langues configurables.
- Libellés traduisibles.
- Formats date.
- Formats nombre.
- Formats devise.

## 24. Multi-devises

- Devise document.
- Devise entreprise.
- Taux.
- Date taux.
- Montants original/fonctionnel lorsque nécessaire.
- Affichage clair.

## 25. PDF

- Génération serveur.
- Aperçu.
- Version imprimable.
- A4.
- Ticket.
- Formats personnalisés.
- PDF archivable.
- Métadonnées.
- Numéro document.

## 26. Impression POS

- Ticket thermique.
- Format configurable.
- Imprimante caisse.
- Logo.
- QR.
- Réimpression selon permission.
- Queue si imprimante indisponible.

## 27. QR codes

- Lien vérification.
- Identifiant document.
- Paiement lorsque intégration disponible.
- Informations minimales.
- Signature/empreinte selon besoin.
- Ne pas exposer de données sensibles inutilement.

## 28. Vérification document

```
SCAN QR
 ↓
PAGE VÉRIFICATION
 ↓
NUMÉRO
 ↓
STATUT
 ↓
DONNÉES PUBLIQUES MINIMALES
```

- Valide.
- Annulé.
- Remplacé.
- Introuvable.
- Accès protégé si document confidentiel.

## 29. Signature électronique

- Signature interne.
- Signature client optionnelle.
- Signature fournisseur optionnelle.
- Horodatage.
- Identité signataire.
- Preuve.
- Statut.

La valeur juridique d'une signature dépend du pays et du dispositif retenu ; le système doit permettre plusieurs niveaux de signature sans présenter automatiquement toute signature comme légalement qualifiée.

## 30. Documents versionnés

- Brouillon version 1.
- Modification → version suivante.
- Document validé figé.
- Nouvelle version ou document correctif.
- Historique.

## 31. Annulation

- Document annulable selon état.
- Motif.
- Utilisateur.
- Date.
- Document reste visible.
- Référence éventuelle au document correctif.
- Audit.

## 32. Correction facture

```
FACTURE ERRONÉE → AVOIR/ANNULATION SELON RÈGLES → NOUVELLE FACTURE
```

- Ne pas écraser une facture finalisée.
- Lien entre documents.
- Audit complet.

## 33. Archivage

- Stockage document.
- Métadonnées.
- Type.
- Numéro.
- Entreprise.
- Client/fournisseur.
- Date.
- Hash optionnel.
- Durée de conservation configurable.
- Accès contrôlé.

## 34. Recherche documentaire

- Numéro.
- Client.
- Fournisseur.
- Date.
- Montant.
- Type.
- Entreprise.
- État.
- Référence commande.
- Référence paiement.

## 35. Exports

- PDF.
- CSV.
- Excel selon besoin.
- ZIP documentaire selon permission.
- Exports comptables.
- Exports fiscaux selon configuration.

## 36. Envoi

- Email.
- Téléchargement.
- Impression.
- Lien sécurisé.
- SMS/notification lorsque intégration disponible.
- Journal d'envoi.
- Statut.

## 37. Templates email

- Facture.
- Devis.
- Relance.
- Reçu.
- Avoir.
- Commande.
- Livraison.
- Relevé.
- Message personnalisé.

## 38. Relances documentaires

- Facture bientôt échue.
- Facture échue.
- Relevé périodique.
- Promesse de paiement.
- Document manquant.
- Échec envoi.

## 39. Paiement intégré au document

- Lien de paiement lorsque disponible.
- Montant.
- Devise.
- Expiration.
- Statut.
- Confirmation.
- Référence.
- Aucun paiement considéré comme réussi sans confirmation.

## 40. Documents restaurant

- Addition.
- Ticket.
- Facture.
- Reçu.
- Bon cuisine selon module.
- Partage addition.
- Pourboire si configuré.

## 41. Documents pharmacie

- Ticket.
- Facture.
- Reçu.
- Bon retour.
- Traçabilité lot selon politique.
- Documents de rappel si module activé.

## 42. Documents supermarché

- Ticket thermique.
- Facture.
- Avoir.
- Bon livraison.
- Reçu.
- QR vérification.

## 43. Documents grossiste

- Devis.
- Commande.
- Proforma.
- Facture.
- Acompte.
- Bon livraison.
- Relevé.
- Avoir.

## 44. Documents services

- Devis prestation.
- Commande.
- Contrat/référence.
- Facture.
- Reçu.
- Rapport prestation optionnel.

## 45. Comptabilité

- Document source identifiable.
- Facture liée à écriture.
- Avoir lié.
- Reçu lié au paiement.
- Document fournisseur lié à dette.
- Écriture non modifiable par édition du PDF.

## 46. Audit

- Création.
- Modification brouillon.
- Validation.
- Numérotation.
- Annulation.
- Avoir.
- Réimpression.
- Téléchargement sensible.
- Signature.
- Envoi.
- Remplacement.
- Archivage.

## 47. Permissions

- Créer devis.
- Valider devis.
- Créer facture.
- Émettre facture.
- Annuler.
- Créer avoir.
- Rembourser.
- Réimprimer.
- Télécharger.
- Modifier template.
- Modifier numérotation.
- Signer.
- Archiver.

## 48. API principales

```
GET/POST /api/v1/quotes
GET/POST /api/v1/orders
GET/POST /api/v1/invoices
POST     /api/v1/invoices/{id}/issue
POST     /api/v1/invoices/{id}/cancel
POST     /api/v1/credit-notes
GET/POST /api/v1/receipts
GET/POST /api/v1/delivery-notes
GET/POST /api/v1/document-templates
GET      /api/v1/documents/{id}/pdf
POST     /api/v1/documents/{id}/send
GET      /api/v1/documents/{id}/verify
GET      /api/v1/document-sequences
```

## 49. Événements

```
QuoteCreated
QuoteAccepted
OrderCreated
InvoiceIssued
InvoiceCancelled
CreditNoteIssued
ReceiptIssued
DeliveryNoteIssued
DocumentSent
DocumentSigned
DocumentArchived
```

## 50. Tests

- Devis.
- Conversion devis→commande.
- Commande→facture.
- Facture comptant.
- Facture crédit.
- Acompte.
- Facture finale.
- Avoir.
- Reçu.
- Bon livraison.
- Bon réception.
- Numérotation concurrente.
- PDF.
- Ticket.
- QR vérification.
- Signature.
- Annulation.
- Versioning.
- Archivage.
- Multi-entreprises.
- Multi-devises.
- Multi-langues.
- Permissions.

## 51. Scénario complet

```
DEVIS 100 000
 ↓
ACCEPTÉ
 ↓
COMMANDE
 ↓
ACOMPTE 30 000
 ↓
LIVRAISON
 ↓
FACTURE 100 000
 ↓
ACOMPTE -30 000
 ↓
SOLDE 70 000
 ↓
PAIEMENT 70 000
 ↓
REÇU
 ↓
CRÉANCE = 0
```

## 52. Critères de sortie

- Devis.
- Proforma.
- Commandes.
- Factures.
- Acomptes.
- Factures finales.
- Avoirs.
- Reçus.
- Livraisons.
- Réceptions.
- Relevés.
- Documents fournisseurs.
- Numérotation.
- Templates.
- PDF.
- Impression.
- QR.
- Signature.
- Versioning.
- Archivage.
- Recherche.
- Exports.
- Envoi.
- Paiement par lien.
- Documents sectoriels.
- Finance.
- Comptabilité.
- Permissions.
- Audit.
- API.
- Tests.

## 53. Étape suivante

Passer à la Phase 31 — Reporting, tableaux de bord, KPIs, analyses commerciales, financières, comptables et opérationnelles. Elle transformera les données NEXORA en informations décisionnelles pour caissier, manager, comptable, direction et groupe.

---

**NEXORA V1.0 — PHASE 30 VALIDÉE → PHASE 31 : REPORTING & ANALYTICS**
