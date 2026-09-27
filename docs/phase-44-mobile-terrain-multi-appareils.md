# NEXORA ONE
## PHASE 44 — MOBILE, TERRAIN & MULTI-APPAREILS
Smartphone, tablette, POS mobile, équipes terrain, synchronisation & offline — NEXORA ONE V1.0
Version 1.0

### 1. Objectif
Étendre NEXORA ONE aux usages mobiles et terrain sans créer une version métier séparée. Les utilisateurs doivent retrouver leurs données, permissions, notifications et tâches sur les appareils adaptés à leur rôle, avec une stratégie offline maîtrisée.

### 2. Principe
```
NEXORA ONE
  ↓
WEB / MOBILE / TABLETTE / POS
  ↓
MÊMES RÈGLES MÉTIER
  ↓
MÊMES PERMISSIONS
  ↓
SYNC
  ↓
DONNÉES CENTRALISÉES
```
- Une même identité utilisateur.
- Une même logique métier.
- Une interface adaptée au contexte.
- Offline uniquement pour les opérations explicitement autorisées.
- Aucune synchronisation ne doit contourner les contrôles serveur.

### 3. Cibles
- Smartphone.
- Tablette.
- Terminal POS mobile.
- Ordinateur portable.
- Appareil partagé.
- Kiosque lorsque pertinent.
- Appareil terrain durci lorsque nécessaire.

### 4. Approche applicative
- Web responsive.
- PWA.
- Application mobile native/hybride selon besoin futur.
- POS optimisé tactile.
- Architecture permettant réutilisation des services backend.

### 5. Profils mobiles
- Dirigeant.
- Manager.
- Comptable.
- Comptable en chef.
- Commercial.
- Livreur.
- Technicien.
- Agent inventaire.
- Caissier.
- Administrateur.

### 6. Dashboard mobile
- KPI.
- Alertes.
- Tâches.
- Notifications.
- Approbations.
- Entreprises.
- Recherche.
- Actions rapides.

### 7. Dirigeant mobile
- Vue groupe.
- Vue entreprise.
- CA.
- Marge.
- Trésorerie.
- Créances/dettes.
- Stock critique.
- Alertes.
- Recommandations.
- Validation.

### 8. Manager mobile
- Ventes.
- Stock.
- Commandes.
- Équipe.
- Tâches.
- Alertes.
- Approbations.
- Rapports courts.

### 9. Comptable mobile
- Créances.
- Dettes.
- Rapprochements.
- Écritures à valider.
- Alertes.
- Documents.
- Approbations.

### 10. Commercial mobile
- Clients.
- Prospects lorsque module activé.
- Commandes.
- Catalogue.
- Prix.
- Disponibilité stock.
- Historique.
- Devis.
- Relances.

### 11. Agent inventaire
- Scanner.
- Produit.
- Quantité.
- Emplacement.
- Lot.
- Expiration.
- Inventaire.
- Écart.
- Photo justificative lorsque utile.

### 12. Livreur
- Tournées.
- Commandes.
- Adresse.
- Contact.
- Statut.
- Preuve livraison.
- Signature lorsque prévue.
- Photo.
- Paiement à la livraison lorsque autorisé.
- Retour.

### 13. Technicien
- Interventions.
- Client.
- Équipement.
- Checklist.
- Pièces.
- Temps.
- Photo.
- Signature.
- Statut.
- Rapport.

### 14. POS mobile
- Catalogue.
- Scanner caméra.
- Panier.
- Client.
- Paiement.
- Crédit.
- Reçu.
- Imprimante mobile.
- Offline.

### 15. Tablette restaurant
- Tables.
- Zones.
- Commande.
- Modificateurs.
- Cuisine.
- Addition.
- Paiement.
- Transfert table.
- Split bill.

### 16. Tablette pharmacie
- Recherche.
- Scanner.
- Lot.
- Expiration.
- Stock.
- Inventaire.
- Prix.
- Alertes.

### 17. Tablette commerce
- Scanner.
- Inventaire.
- Réception.
- Transfert.
- Étiquetage.
- Prix.
- Rayons.

### 18. Appareil partagé
- Connexion rapide.
- PIN contrôlé.
- Verrouillage automatique.
- Changement utilisateur.
- Aucun mélange de sessions.
- Audit.

### 19. Biométrie
- Biométrie du système pour déverrouillage local lorsque supportée.
- Ne pas stocker les données biométriques brutes dans NEXORA ONE.
- Fallback PIN/mot de passe.
- Politique appareil.

### 20. Notifications mobiles
- Push.
- Centre notifications.
- Priorité.
- Deep link.
- Silence.
- Résumé.
- Entreprise.
- Tâche.
- Approbation.

### 21. Approbations mobiles
- Paiement.
- Dépense.
- Achat.
- Remise exceptionnelle.
- Remboursement.
- Transfert stock.
- Modification sensible.
- Workflow configurable.

### 22. Signature
- Signature client.
- Signature livraison.
- Signature intervention.
- Horodatage.
- Identité utilisateur.
- Document associé.
- Audit.

### 23. Photo / caméra
- Code-barres.
- QR.
- Document.
- Preuve livraison.
- Inventaire.
- Équipement.
- Pièce justificative.

### 24. GPS
- Position lorsque nécessaire.
- Suivi livraison lorsque activé.
- Géolocalisation volontaire et justifiée.
- Pas de suivi permanent sans configuration/politique appropriée.
- Historique limité.

### 25. Mode offline
```
OFFLINE
  ↓
DONNÉES AUTORISÉES EN CACHE
  ↓
TRANSACTION LOCALE
  ↓
QUEUE
  ↓
RECONNEXION
  ↓
VALIDATION SERVEUR
  ↓
SYNC
```
- Indicateur clair.
- Données cache limitées.
- Chiffrement local.
- Expiration cache.
- Queue persistante.
- Identifiants uniques.

### 26. Opérations offline autorisées
- Vente POS selon politique.
- Inventaire.
- Consultation cache.
- Commande terrain.
- Checklist.
- Collecte données.
- Création brouillon.
- Signature locale lorsque prévue.

### 27. Opérations offline interdites ou limitées
- Actions financières critiques nécessitant validation serveur.
- Modification de permissions.
- Gestion utilisateurs sensibles.
- Remboursements à risque.
- Transferts financiers.
- Opérations nécessitant solde temps réel.

### 28. Synchronisation
```
LOCAL
 ↓
QUEUE
 ↓
AUTHENTIFICATION
 ↓
VALIDATION
 ↓
IDEMPOTENCE
 ↓
CONFLIT ?
 ├→ NON → APPLY
 └→ OUI → RESOLUTION
 ↓
CONFIRMATION
```

### 29. Conflits
- Version objet.
- Timestamp.
- Priorité serveur.
- Règle métier.
- Résolution manuelle.
- Journal.
- Aucun écrasement silencieux.

### 30. Données sensibles offline
- Minimisation.
- Chiffrement stockage.
- Expiration.
- Effacement distant lorsque possible.
- Verrouillage.
- Pas de secrets inutiles.

### 31. Sécurité appareil
- PIN/biométrie système.
- Verrouillage.
- Session courte.
- Révocation appareil.
- Liste appareils.
- Détection appareil compromis lorsque supportée.
- Mises à jour.

### 32. Gestion appareils
- Nom.
- Type.
- OS.
- Version app.
- Dernière activité.
- Dernière synchronisation.
- Entreprise.
- Caisse.
- Statut.
- Révocation.

### 33. MDM / entreprise
- Compatibilité MDM lorsque nécessaire.
- Politiques sécurité.
- Effacement à distance selon infrastructure.
- Applications autorisées.
- Certificats.

### 34. Impression mobile
- Bluetooth.
- Wi-Fi.
- Imprimante réseau.
- Ticket.
- A4.
- Réimpression contrôlée.
- État imprimante.

### 35. Périphériques
- Scanner Bluetooth.
- Scanner USB lorsque supporté.
- Balance.
- Lecteur carte via fournisseur.
- Tiroir caisse.
- Écran client.

### 36. Synchronisation documents
- Upload différé.
- Compression.
- Reprise.
- Hash.
- Statut.
- Suppression cache après confirmation.

### 37. Performance mobile
- Chargement rapide.
- Cache.
- Lazy loading.
- Images optimisées.
- Pagination.
- Listes virtualisées.
- Batterie.
- Réseau lent.

### 38. Réseaux faibles
- 2G/3G lorsque encore disponible.
- Latence élevée.
- Requêtes compactes.
- Retry.
- Compression.
- Mode offline.

### 39. Batterie
- Éviter GPS permanent.
- Sync en arrière-plan contrôlée.
- Batch.
- Fréquence configurable.
- Respect politiques OS.

### 40. Multi-entreprises mobile
- Sélecteur entreprise.
- Entreprise active visible.
- Permissions par entreprise.
- Dashboard groupe si autorisé.
- Aucune donnée interentreprise dans le cache d'un utilisateur non autorisé.

### 41. Deep links
- Notification → document.
- Tâche → objet.
- Commande → client.
- Paiement → transaction.
- Alerte → rapport.
- Approbation → workflow.

### 42. Partage
- Partager document via mécanisme OS lorsque autorisé.
- Liens temporaires.
- Permissions.
- Expiration.
- Audit.

### 43. Accessibilité mobile
- Lecteur d'écran.
- Taille texte.
- Contraste.
- Zones tactiles.
- Orientation.
- Réduction animations.
- Navigation clavier sur appareils compatibles.

### 44. Internationalisation
- Français.
- Anglais.
- Créole haïtien lorsque prévu.
- Dates.
- Nombres.
- Devise.
- Fuseau horaire.
- Traductions hors ligne nécessaires.

### 45. Mise à jour
- Version.
- Mise à jour contrôlée.
- Compatibilité backend.
- Migration cache.
- Force update pour sécurité critique lorsque nécessaire.
- Rollback lorsque possible.

### 46. Crash / diagnostics
- Crash report minimisé.
- Version.
- OS.
- Appareil.
- Étape.
- Correlation ID.
- Aucune donnée sensible inutile.
- Opt-in lorsque nécessaire.

### 47. Mobile analytics
- Écran.
- Performance.
- Erreurs.
- Usage fonctionnel.
- Adoption.
- Offline.
- Sync.
- Batterie lorsque pertinent.

Les analytics doivent être minimisés, transparents et conformes aux politiques de confidentialité.

### 48. API principales
```
GET/POST /api/v1/devices
POST     /api/v1/devices/{id}/revoke
GET      /api/v1/mobile/sync
POST     /api/v1/mobile/sync/push
POST     /api/v1/mobile/sync/pull
GET      /api/v1/mobile/conflicts
POST     /api/v1/mobile/conflicts/{id}/resolve
GET      /api/v1/mobile/config
POST     /api/v1/mobile/crash-reports
GET      /api/v1/mobile/notifications
GET      /api/v1/mobile/tasks
```

### 49. Événements
```
DeviceRegistered
DeviceRevoked
MobileSyncStarted
MobileSyncCompleted
MobileSyncFailed
OfflineTransactionQueued
OfflineTransactionSynced
SyncConflictCreated
PushSent
PushOpened
MobileCrashReported
```

### 50. Tests
- Smartphone.
- Tablette.
- POS mobile.
- Appareil partagé.
- PIN.
- Biométrie système.
- Push.
- Approbation.
- Scanner caméra.
- QR.
- Photo.
- Signature.
- GPS.
- Offline.
- Reconnexion.
- Conflit.
- Idempotence.
- Données sensibles cache.
- Révocation appareil.
- MDM.
- Impression.
- Scanner.
- Balance.
- Réseau faible.
- Batterie.
- Multi-entreprises.
- Deep links.
- Accessibilité.
- Internationalisation.
- Update.
- Crash.
- Performance.

### 51. Scénario terrain
```
COMMERCIAL
  ↓
CLIENT
  ↓
COMMANDE
  ↓
RÉSEAU FAIBLE
  ↓
BROUILLON / DONNÉES LOCALES
  ↓
RECONNEXION
  ↓
SYNC
  ↓
STOCK
  ↓
FACTURATION
  ↓
NOTIFICATION
  ↓
SUIVI
```

### 52. Scénario livraison
```
TOURNÉE
 ↓
COMMANDE
 ↓
NAVIGATION
 ↓
LIVRAISON
 ↓
SIGNATURE / PHOTO
 ↓
PAIEMENT SI AUTORISÉ
 ↓
SYNC
 ↓
STATUT COMMANDE
 ↓
NOTIFICATION CLIENT
```

### 53. Scénario inventaire
```
SCAN
 ↓
PRODUIT
 ↓
QUANTITÉ
 ↓
LOT / EXPIRATION
 ↓
EMPLACEMENT
 ↓
VALIDATION
 ↓
OFFLINE SI BESOIN
 ↓
SYNC
 ↓
ÉCART
 ↓
RAPPROCHEMENT
```

### 54. Critères de sortie
- Cibles appareils.
- Approche app.
- Profils mobiles.
- Dashboards.
- Dirigeant.
- Manager.
- Comptable.
- Commercial.
- Inventaire.
- Livreur.
- Technicien.
- POS mobile.
- Restaurant.
- Pharmacie.
- Commerce.
- Appareil partagé.
- Biométrie système.
- Notifications.
- Approbations.
- Signature.
- Caméra.
- GPS.
- Offline.
- Sync.
- Conflits.
- Données sensibles.
- Sécurité appareil.
- Gestion appareils.
- MDM.
- Impression.
- Périphériques.
- Documents.
- Performance.
- Réseaux faibles.
- Batterie.
- Multi-entreprises.
- Deep links.
- Partage.
- Accessibilité.
- Internationalisation.
- Mises à jour.
- Crash.
- Analytics.
- API.
- Événements.
- Tests.

### 55. Étape suivante
Passer à la Phase 45 — Gestion avancée des documents, GED, archivage et signature. Elle centralisera les factures, reçus, contrats, pièces comptables, justificatifs et documents opérationnels avec recherche, versioning, permissions, OCR lorsque disponible et conservation contrôlée.

NEXORA ONE V1.0 — PHASE 44 VALIDÉE → PHASE 45 : GED, DOCUMENTS & SIGNATURE
