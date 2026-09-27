# NEXORA ONE
## PHASE 45 — GED, DOCUMENTS, ARCHIVAGE & SIGNATURE
Gestion électronique des documents, OCR, versions, signatures & conservation — NEXORA ONE V1.0
Version 1.0

### 1. Objectif
Centraliser les documents produits ou utilisés par NEXORA ONE afin qu'une entreprise puisse retrouver rapidement ses factures, reçus, contrats, justificatifs, pièces comptables et documents opérationnels, avec contrôle d'accès, historique, versions, recherche, archivage et signature.

### 2. Principe
```
DOCUMENT
 ↓
IMPORT / CRÉATION
 ↓
CLASSIFICATION
 ↓
INDEXATION
 ↓
OCR SI NÉCESSAIRE
 ↓
VALIDATION
 ↓
VERSION
 ↓
SIGNATURE / APPROBATION
 ↓
ARCHIVAGE
 ↓
RECHERCHE / AUDIT
```
- Le document original doit rester identifiable.
- Les métadonnées ne doivent pas remplacer la pièce source.
- Les documents sensibles sont protégés par permissions.
- La suppression doit respecter la rétention applicable.

### 3. Types de documents
- Factures ventes.
- Factures achats.
- Reçus.
- Avoirs.
- Devis.
- Bons commande.
- Bons livraison.
- Contrats.
- Pièces comptables.
- Justificatifs dépenses.
- Relevés.
- Documents fiscaux.
- Documents clients.
- Documents fournisseurs.
- Documents RH selon module et permissions.
- Documents internes.
- Photos.
- PDF.
- Scans.

### 4. Classement
- Entreprise.
- Établissement.
- Module.
- Type.
- Année.
- Exercice.
- Client.
- Fournisseur.
- Transaction.
- Projet.
- Catégorie.
- Tags.

### 5. Métadonnées
- Nom.
- Type.
- Taille.
- Date création.
- Date document.
- Auteur.
- Entreprise.
- Objet lié.
- Référence.
- Tags.
- Statut.
- Version.
- Signature.
- Empreinte/hash lorsque pertinent.

### 6. Documents liés
```
FACTURE
 ├── CLIENT
 ├── VENTE
 ├── PAIEMENT
 ├── ÉCRITURE
 └── JUSTIFICATIF
```
- Un document peut être lié à plusieurs objets selon règles.
- Les liens doivent rester traçables.
- La suppression d'un objet métier ne doit pas supprimer automatiquement une pièce légalement conservée.

### 7. Upload
- Glisser-déposer.
- Sélection fichier.
- Caméra mobile.
- Scan.
- Import automatique.
- Upload par lots.
- Reprise après interruption.
- Progression.

### 8. Formats
- PDF.
- PDF/A lorsque archivage requis.
- JPG/JPEG.
- PNG.
- WebP lorsque approprié.
- TIFF lorsque nécessaire.
- DOCX.
- XLSX.
- CSV.
- Formats définis par connecteur.

### 9. Limites
- Taille maximale configurable.
- Nombre fichiers par lot.
- Types autorisés.
- Quotas par abonnement.
- Quotas par entreprise.
- Protection contre fichiers malveillants.

### 10. Sécurité fichiers
- Scan antivirus/antimalware.
- MIME réel vérifié.
- Extension non fiable.
- Stockage privé.
- Accès signé/temporaire lorsque nécessaire.
- Chiffrement en transit.
- Chiffrement au repos selon infrastructure.

### 11. OCR
- Extraction texte.
- Détection langue.
- Numéro facture.
- Date.
- Fournisseur.
- Montant.
- Taxes.
- Devise.
- Référence.
- Confiance extraction.

L'OCR sert à accélérer la saisie et la recherche. Les données extraites doivent être validées avant d'alimenter automatiquement des opérations financières sensibles.

### 12. OCR factures
```
DOCUMENT
 ↓
OCR
 ↓
CHAMPS PROPOSÉS
 ↓
VALIDATION
 ↓
FACTURE / ACHAT
```

### 13. Recherche
- Nom.
- Référence.
- Texte OCR.
- Client.
- Fournisseur.
- Numéro facture.
- Date.
- Montant.
- Tags.
- Objet lié.
- Entreprise.
- Type.

### 14. Recherche avancée
- AND/OR.
- Phrase exacte.
- Plage dates.
- Montant.
- Statut.
- Version.
- Signé/non signé.
- Filtres enregistrés.
- Recherche par contenu.

### 15. Aperçu
- PDF viewer.
- Images.
- Documents compatibles.
- Zoom.
- Rotation.
- Miniatures.
- Recherche texte.
- Pages.
- Téléchargement selon permission.

### 16. Versioning
- Version 1.
- Version 2.
- Auteur.
- Date.
- Motif.
- Différence lorsque possible.
- Version active.
- Restaurer comme nouvelle version.

### 17. Historique
- Créé.
- Modifié.
- Consulté lorsque audit requis.
- Téléchargé lorsque audit requis.
- Partagé.
- Signé.
- Approuvé.
- Archivé.
- Restauré.
- Supprimé selon politique.

### 18. Verrouillage
- Document en validation.
- Document signé.
- Document clôturé.
- Verrouillage métier.
- Déverrouillage soumis à permission.

### 19. Signature électronique
- Signature simple.
- Signature avancée lorsque fournisseur conforme disponible.
- Signature multiple.
- Ordre des signataires.
- Invitation.
- Rappel.
- Expiration.
- Statut.

Le niveau de signature et sa valeur juridique dépendent du cadre applicable et du prestataire utilisé. NEXORA ONE doit conserver les preuves et métadonnées disponibles sans prétendre qu'une signature possède automatiquement une qualification juridique particulière.

### 20. Workflow signature
```
DOCUMENT
 ↓
SIGNATAIRES
 ↓
INVITATION
 ↓
AUTHENTIFICATION
 ↓
SIGNATURE
 ↓
PREUVE
 ↓
DOCUMENT FINAL
 ↓
ARCHIVAGE
```

### 21. Signataires
- Utilisateur NEXORA ONE.
- Contact externe.
- Ordre.
- Rôle.
- Date limite.
- Statut.
- Relance.

### 22. Preuve signature
- Identité déclarée.
- Authentification utilisée.
- Date/heure.
- Document hash.
- Version signée.
- Journal.
- Preuves du prestataire lorsqu'elles existent.

### 23. Approbation interne
- Dépense.
- Achat.
- Contrat.
- Facture.
- Remboursement.
- Document comptable.
- Workflow par montant.
- Workflow par rôle.

### 24. Workflows
```
BROUILLON
 ↓
REVUE
 ↓
APPROBATION
 ↓
SIGNATURE
 ↓
FINAL
 ↓
ARCHIVE
```

### 25. Documents financiers
- Factures.
- Avoirs.
- Reçus.
- Justificatifs.
- Relevés.
- Pièces de caisse.
- Pièces bancaires.
- Pièces de rapprochement.
- Documents fiscaux.

### 26. Documents comptables
- Pièces journal.
- Justificatifs écriture.
- Grand livre export.
- Balance.
- Déclarations.
- Clôture.
- Pièces d'inventaire.

### 27. Archivage
- Actif.
- Archivé.
- Gelé.
- Rétention expirée.
- Suppression éligible.
- Legal hold si fonctionnalité applicable.

### 28. Rétention
- Politique globale.
- Politique entreprise.
- Type document.
- Date de départ.
- Durée.
- Exception.
- Motif.
- Audit.

La politique doit être configurable et alignée sur les obligations applicables. Un document soumis à conservation obligatoire ne doit pas être supprimé par une règle générique.

### 29. Legal hold
- Blocage suppression.
- Motif.
- Périmètre.
- Autorité.
- Date début.
- Date fin.
- Audit.

### 30. Suppression
- Soft delete lorsque approprié.
- Corbeille contrôlée.
- Validation.
- Purge définitive selon politique.
- Journal.
- Protection des documents sous rétention/hold.

### 31. Partage
- Interne.
- Externe.
- Lien temporaire.
- Expiration.
- Téléchargement contrôlé.
- Lecture seule.
- Mot de passe optionnel.
- Révocation.
- Audit.

### 32. Dossiers
- Dossiers personnalisés.
- Sous-dossiers.
- Tags.
- Favoris.
- Partage.
- Permissions héritées contrôlées.

### 33. Permissions
- Lecture.
- Prévisualisation.
- Téléchargement.
- Création.
- Modification.
- Version.
- Signature.
- Partage.
- Suppression.
- Archivage.
- Administration.

### 34. Scope
- Groupe.
- Entreprise.
- Établissement.
- Département.
- Utilisateur.
- Objet métier.

### 35. GED multi-entreprises
- Bibliothèque groupe.
- Bibliothèque entreprise.
- Documents interentreprises selon permission.
- Aucune fuite entre tenants.
- Filtres par entreprise.
- Archivage indépendant.

### 36. Documents offline
- Capture locale.
- File upload.
- Compression.
- Chiffrement cache.
- Synchronisation.
- Reprise.
- Suppression cache après confirmation.

### 37. Mobile
- Scan caméra.
- Photo justificative.
- Signature.
- Recherche.
- Aperçu.
- Upload.
- Validation.
- Notifications.

### 38. OCR et facturation
- Suggestion automatique de fournisseur.
- Détection numéro.
- Détection total.
- Détection taxes.
- Détection doublon.
- Création brouillon.
- Validation humaine.

### 39. Détection doublons
- Hash fichier.
- Numéro document.
- Fournisseur/client.
- Date.
- Montant.
- Score.
- Alerte.
- Décision utilisateur.

### 40. Documents générés
- Factures.
- Reçus.
- Devis.
- Bons.
- Rapports.
- États financiers.
- Attestations selon module.
- Exports.

### 41. Templates
- Logo.
- En-tête.
- Pied de page.
- Numérotation.
- Langue.
- Devise.
- Conditions.
- Champs personnalisés.
- Version template.

### 42. Impression
- Impression directe.
- PDF.
- Imprimante entreprise.
- Imprimante POS.
- Copies.
- Réimpression contrôlée.

### 43. Watermark
- Brouillon.
- Copie.
- Confidentiel lorsque configuré.
- Document annulé.
- Aperçu.
- Marquage téléchargements si politique.

### 44. Export
- PDF.
- ZIP contrôlé.
- CSV/XLSX pour métadonnées.
- API.
- Export entreprise.
- Export groupe selon permission.

### 45. Audit GED
- Upload.
- Lecture.
- Téléchargement.
- Partage.
- Modification.
- Version.
- Signature.
- Approbation.
- Archivage.
- Suppression.
- Permission.

### 46. Intégrité
- Hash.
- Taille.
- Version.
- Source.
- Date.
- Contrôle intégrité.
- Détection fichier corrompu.

### 47. Backup documents
- Backup séparé.
- Chiffrement.
- Versioning.
- Restauration.
- Test restauration.
- Politique rétention.
- RPO/RTO selon infrastructure.

### 48. Migration documents
- Import ancien système.
- Mapping dossiers.
- Métadonnées.
- Association objet.
- Doublons.
- OCR optionnel.
- Validation.
- Rapport migration.

### 49. API principales
```
POST /api/v1/documents
GET  /api/v1/documents/{id}
PUT  /api/v1/documents/{id}
POST /api/v1/documents/{id}/versions
GET  /api/v1/documents/{id}/versions
POST /api/v1/documents/{id}/archive
POST /api/v1/documents/{id}/restore
POST /api/v1/documents/{id}/share
POST /api/v1/documents/{id}/signature
GET  /api/v1/documents/search
POST /api/v1/documents/ocr
GET  /api/v1/document-workflows
POST /api/v1/document-workflows
GET  /api/v1/document-audit
```

### 50. Événements
```
DocumentUploaded
DocumentClassified
DocumentOcrCompleted
DocumentVersionCreated
DocumentShared
DocumentViewed
DocumentDownloaded
DocumentSubmitted
DocumentApproved
DocumentRejected
SignatureRequested
SignatureCompleted
DocumentArchived
DocumentRestored
DocumentDeletionRequested
DocumentDeleted
```

### 51. Tests
- Upload.
- Formats.
- Taille.
- Scan sécurité.
- OCR.
- Recherche.
- Métadonnées.
- Aperçu.
- Versioning.
- Historique.
- Verrouillage.
- Signature.
- Preuves.
- Approbation.
- Workflow.
- Archivage.
- Rétention.
- Legal hold.
- Suppression.
- Partage.
- Permissions.
- Multi-entreprises.
- Offline.
- Mobile.
- Doublons.
- Documents générés.
- Templates.
- Impression.
- Watermark.
- Export.
- Audit.
- Intégrité.
- Backup.
- Restauration.
- Migration.

### 52. Scénario facture fournisseur
```
FACTURE REÇUE
 ↓
UPLOAD / SCAN
 ↓
OCR
 ↓
DÉTECTION FOURNISSEUR
 ↓
MONTANT / TAXES
 ↓
DOUBLON ?
 ↓
VALIDATION
 ↓
APPROBATION
 ↓
COMPTABILISATION
 ↓
ARCHIVAGE
```

### 53. Scénario contrat
```
CONTRAT
 ↓
IMPORT
 ↓
REVUE
 ↓
SIGNATAIRES
 ↓
SIGNATURES
 ↓
VERSION FINALE
 ↓
ARCHIVAGE
 ↓
ALERTE ÉCHÉANCE
```

### 54. Critères de sortie
- Types documents.
- Classement.
- Métadonnées.
- Documents liés.
- Upload.
- Formats.
- Sécurité fichiers.
- OCR.
- Recherche.
- Aperçu.
- Versioning.
- Historique.
- Verrouillage.
- Signature.
- Preuves.
- Approbation.
- Workflows.
- Documents financiers.
- Documents comptables.
- Archivage.
- Rétention.
- Legal hold.
- Suppression.
- Partage.
- Dossiers.
- Permissions.
- Scope.
- GED multi-entreprises.
- Offline.
- Mobile.
- OCR facturation.
- Doublons.
- Documents générés.
- Templates.
- Impression.
- Watermark.
- Export.
- Audit.
- Intégrité.
- Backup.
- Migration.
- API.
- Événements.
- Tests.

### 55. Étape suivante
Passer à la Phase 46 — Écosystème partenaires, intégrations, API publique et marketplace. Elle permettra à NEXORA ONE de se connecter aux services utilisés par les entreprises et de construire un écosystème autour de la plateforme.

NEXORA ONE V1.0 — PHASE 45 VALIDÉE → PHASE 46 : API, INTÉGRATIONS & ÉCOSYSTÈME
