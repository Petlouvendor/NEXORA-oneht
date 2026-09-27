# NEXORA — PHASE 11 — SÉCURITÉ, AUDIT, CONFORMITÉ & ANTI-FRAUDE

**Protection des données, contrôle des opérations et traçabilité**
Version 1.0

## 1. Objectif

Définir la sécurité de NEXORA comme une fonction centrale et non comme une couche ajoutée
après développement. Le système traite des ventes, paiements, stocks, créances, dettes,
données comptables et informations de plusieurs entreprises : toute opération sensible doit
donc être autorisée, traçable et contrôlable.

## 2. Principes

- Deny by default.
- Moindre privilège.
- Séparation des responsabilités.
- Traçabilité des opérations sensibles.
- Aucune suppression silencieuse d'un historique financier.
- Protection des données en transit et au repos selon le contexte.
- Validation côté serveur.
- Sécurité identique en local, cloud et hybride.
- Les mécanismes anti-fraude alertent sans remplacer le jugement humain.
- Les exigences légales locales doivent être validées avec un professionnel compétent.

## 3. Périmètre de sécurité

- Identité et authentification.
- Autorisations et scopes.
- Multi-entreprises.
- POS.
- Finance.
- Comptabilité.
- Stock.
- Paiements.
- Documents.
- API.
- Base de données.
- Offline.
- Synchronisation.
- Sauvegardes.
- Audit.
- Exports.
- Intégrations externes.

## 4. Modèle d'accès

```
USER → RÔLE → PERMISSION → SCOPE → RESSOURCE → ACTION → AUDIT
```

- Rôle global si nécessaire.
- Rôle par entreprise.
- Rôle par établissement.
- Permissions fines.
- Scopes caisse/entrepôt.
- Permissions temporaires si activées.
- Accès groupe explicitement accordé.

## 5. Séparation des responsabilités

- Création d'une dépense ≠ validation automatique.
- Création d'une commande ≠ réception automatique.
- Réception ≠ paiement.
- Paiement ≠ rapprochement obligatoire par la même personne.
- Écriture ≠ clôture.
- Ajustement stock important ≠ validation par le même utilisateur lorsque la politique exige
  une seconde approbation.
- Consolidation ≠ modification des livres sources.

## 6. Authentification

- Mot de passe robuste.
- Hash sécurisé.
- MFA configurable.
- Expiration et révocation des sessions.
- Protection contre essais répétés.
- Réinitialisation sécurisée.
- Gestion des appareils.
- Déconnexion globale.
- Réauthentification pour actions critiques.

## 7. Authentification renforcée

- MFA obligatoire pour administrateurs selon politique.
- MFA pour comptable en chef si activé.
- Réauthentification avant clôture/réouverture.
- Réauthentification avant modification de permissions.
- Réauthentification avant opérations financières exceptionnelles.
- Possibilité de validation secondaire.

## 8. Gestion des rôles

- Administrateur système.
- Administrateur groupe.
- Direction.
- Comptable en chef.
- Comptable.
- Manager.
- Responsable stock.
- Caissier.
- Vendeur.
- Auditeur.
- Rôles personnalisés.

Les noms peuvent être adaptés à l'entreprise ; les permissions sous-jacentes restent
structurées.

## 9. Sécurité multi-entreprises

- Isolation par entreprise.
- Vérification serveur du scope.
- Aucun accès par simple modification d'un identifiant.
- Audit des accès groupe.
- Exports limités au scope.
- Intercompany explicitement autorisé.
- Consolidation accessible uniquement aux rôles autorisés.

## 10. Audit — principe

L'audit doit permettre de répondre à : qui a fait quoi, quand, sur quelle entreprise, depuis
quel contexte, avec quelle valeur avant/après et avec quel résultat.

```
WHO + WHEN + WHERE + WHAT + BEFORE + AFTER + RESULT + SOURCE
```

## 11. Événements audités

- Connexion/déconnexion.
- Échec de connexion.
- Changement de rôle.
- Changement de permission.
- Changement de scope.
- Création/modification entreprise.
- Modification produit.
- Modification prix.
- Vente.
- Annulation.
- Retour.
- Remboursement.
- Paiement.
- Affectation paiement.
- Ajustement stock.
- Inventaire.
- Dépense.
- Validation facture.
- Écriture comptable.
- Extourne.
- Clôture.
- Réouverture.
- Rapprochement.
- Intercompany.
- Consolidation.
- Export sensible.
- Modification de configuration.

## 12. Journal d'audit

- Identifiant événement.
- Date/heure.
- Utilisateur.
- Entreprise.
- Établissement.
- Terminal/appareil lorsque disponible.
- Adresse réseau selon politique.
- Type d'action.
- Ressource.
- Identifiant ressource.
- Ancienne valeur si nécessaire.
- Nouvelle valeur si nécessaire.
- Résultat.
- Motif.
- Correlation ID.

Les données d'audit doivent elles-mêmes être protégées contre modification non autorisée.

## 13. Audit des opérations financières

- Chaque vente validée possède une source.
- Chaque paiement possède une référence.
- Chaque écriture possède sa source métier.
- Chaque correction possède sa justification.
- Les annulations sont historisées.
- Les clôtures sont historisées.
- Les réouvertures sont fortement contrôlées.

## 14. Anti-fraude POS

- Détection d'annulations fréquentes.
- Détection de remboursements anormaux.
- Remises inhabituelles.
- Modification répétée de prix.
- Ventes annulées après paiement.
- Écarts de caisse.
- Transactions inhabituelles par heure.
- Activité anormale par utilisateur.
- Utilisation inhabituelle d'un terminal.
- Ventes à crédit inhabituelles.

## 15. Anti-fraude stock

- Ajustements fréquents.
- Pertes répétées.
- Écarts inventaire.
- Sorties manuelles.
- Transferts inhabituels.
- Produits à forte valeur.
- Mouvements hors horaires.
- Annulations de mouvements.
- Écarts entre réception et facture.

## 16. Anti-fraude finance

- Paiements exceptionnels.
- Paiements fractionnés inhabituels.
- Dépenses répétées juste sous un seuil.
- Modifications de bénéficiaire/fournisseur.
- Remboursements inhabituels.
- Écritures manuelles inhabituelles.
- Extournes répétées.
- Écarts de rapprochement.
- Transactions intercompany atypiques.

## 17. Moteur de détection

```
TRANSACTION
   ↓
RÈGLES
   ↓
SCORE / SIGNAL
   ├── NORMAL
   ├── À SURVEILLER
   └── CRITIQUE
          ↓
      ALERTE / REVUE
```

- Règles configurables.
- Seuils configurables.
- Fenêtres temporelles.
- Segmentation par entreprise.
- Historique comportemental.
- Possibilité de marquer un faux positif.
- Aucune suppression de signal sans audit.

## 18. Score de risque

NEXORA peut attribuer un score de risque aux événements sensibles, sans prétendre déterminer
automatiquement une fraude.

- Score faible.
- Score moyen.
- Score élevé.
- Score critique.
- Motifs associés.
- Règles ayant déclenché le score.
- Révision humaine.

## 19. Approbations

- Seuils par montant.
- Seuils par type d'opération.
- Approbation simple.
- Double approbation.
- Approbateur autorisé.
- Expiration d'une demande.
- Refus motivé.
- Historique.

## 20. Workflows sensibles

```
DEMANDE → APPROBATION → EXÉCUTION → AUDIT
```

- Dépense.
- Remboursement.
- Remise exceptionnelle.
- Ajustement stock important.
- Dépassement crédit.
- Réouverture période.
- Écriture manuelle sensible.
- Modification configuration financière.

## 21. Protection des données

- Minimisation des données.
- Contrôle d'accès.
- Chiffrement lorsque nécessaire.
- Protection des sauvegardes.
- Protection des exports.
- Politique de conservation.
- Suppression/archivage contrôlé.
- Masquage des données sensibles dans les interfaces non autorisées.

## 22. Données personnelles

- Identifier les données personnelles réellement nécessaires.
- Limiter les accès.
- Limiter les exports.
- Journaliser les consultations sensibles lorsque pertinent.
- Prévoir mécanismes de rectification/archivage selon obligations applicables.
- Ne pas collecter des informations inutiles.

Pour les données de santé, financières ou autres données réglementées, les exigences de
conservation, confidentialité et accès doivent être validées selon le droit applicable.

## 23. Paiements et intégrations

- Ne pas stocker de données de carte bancaire sensibles si le prestataire de paiement peut
  les conserver.
- Utiliser les mécanismes sécurisés du fournisseur.
- Vérifier signatures de webhooks.
- Empêcher la réutilisation d'événements.
- Conserver identifiants de transaction.
- Journaliser les changements de statut.

## 24. API et sécurité applicative

- TLS.
- Validation stricte.
- Authentification.
- Autorisation par ressource.
- Rate limiting.
- Protection injection.
- Protection XSS/CSRF selon architecture.
- Contrôle uploads.
- Limites de taille.
- Gestion des erreurs sans fuite de secrets.
- Headers de sécurité.

## 25. Sécurité base de données

- Privilèges minimaux.
- Pas d'accès public.
- Comptes séparés selon fonction.
- Migrations contrôlées.
- Sauvegardes chiffrées.
- Tests de restauration.
- Contrôle des connexions.
- Surveillance des anomalies.

## 26. Sécurité offline

- Appareil identifié.
- Données locales minimisées.
- Chiffrement local si nécessaire.
- Session expirée.
- Révocation appareil.
- Transactions uniques.
- Queue protégée.
- Contrôle d'intégrité.
- Synchronisation sécurisée.
- Audit.

## 27. Sécurité des exports

- Permission d'export.
- Filtres de scope.
- Formats contrôlés.
- Journalisation.
- Fichiers temporaires protégés.
- Expiration des liens temporaires.
- Filigrane éventuel pour documents sensibles.
- Interdiction d'exporter hors périmètre.

## 28. Sécurité des sauvegardes

- Chiffrement.
- Copies hors site.
- Rétention.
- Contrôle d'accès.
- Tests de restauration.
- Surveillance.
- Procédure de récupération.
- Séparation des accès sauvegarde/production.

## 29. Gestion des incidents

```
DÉTECTION → TRIAGE → CONFINEMENT → CORRECTION → RÉCUPÉRATION → POST-MORTEM
```

- Incident sécurité.
- Incident financier.
- Incident synchronisation.
- Fuite potentielle.
- Compte compromis.
- Panne critique.
- Escalade.
- Responsable.
- Chronologie.
- Actions correctives.

## 30. Compte compromis

- Révoquer sessions.
- Réinitialiser secrets si nécessaire.
- Bloquer appareil.
- Forcer changement mot de passe.
- Vérifier actions récentes.
- Examiner audit.
- Notifier responsables selon procédure.
- Restaurer uniquement si nécessaire.

## 31. Conformité et gouvernance

- Définir les obligations applicables par pays/secteur.
- Paramétrer périodes et conservation.
- Conserver preuves d'audit.
- Contrôler accès aux données sensibles.
- Documenter politiques.
- Prévoir revue périodique.
- Faire valider les exigences légales par des professionnels compétents.

NEXORA doit être conçu pour la conformité, mais le logiciel ne doit pas prétendre garantir à
lui seul la conformité légale d'une entreprise.

## 32. Fiscalité et documents

- Taxes configurables.
- Numérotation contrôlée.
- Historique des documents.
- Pièces justificatives.
- Périodes.
- Rapports fiscaux selon besoins et cadre applicable.
- Exports structurés.
- Archivage.

## 33. Contrôles comptables

- Débit = crédit.
- Périodes clôturées protégées.
- Numérotation contrôlée.
- Sources des écritures.
- Extournes.
- Rapprochements.
- Contrôle des soldes.
- Contrôle intercompany.
- Audit des ajustements.

## 34. Contrôles de caisse

- Fond initial.
- Sessions individuelles.
- Solde théorique.
- Comptage réel.
- Écart.
- Justification.
- Approbation selon seuil.
- Audit.
- Rapport de clôture.

## 35. Contrôles fournisseurs

- Création fournisseur.
- Validation des coordonnées sensibles.
- Modification bénéficiaire.
- Historique des changements.
- Paiements contrôlés.
- Double validation selon seuil.
- Détection de doublons.

## 36. Sécurité des administrateurs

- Comptes nominatifs.
- MFA.
- Permissions minimales.
- Actions administratives auditées.
- Pas de compte partagé.
- Réauthentification.
- Revue périodique des droits.
- Désactivation immédiate des comptes inutilisés.

## 37. Tests de sécurité

- Tests d'authentification.
- Tests d'autorisation.
- Escalade de privilèges.
- Isolation multi-tenant.
- Injection.
- XSS.
- CSRF.
- Brute force.
- Gestion sessions.
- Uploads.
- Webhooks.
- Offline/synchronisation.
- Exports.
- Sauvegardes/restauration.

## 38. Tests anti-fraude

- Remise anormale.
- Annulation répétée.
- Remboursement inhabituel.
- Écart caisse.
- Ajustement stock inhabituel.
- Dépense proche d'un seuil.
- Paiement fournisseur inhabituel.
- Modification fournisseur.
- Extourne répétée.
- Intercompany atypique.

## 39. Tests d'audit

- Chaque action critique crée un événement.
- Événement lié à la bonne entreprise.
- Ancienne/nouvelle valeur correcte.
- Utilisateur correct.
- Horodatage correct.
- Impossible de modifier sans trace.
- Recherche d'audit filtrée par scope.
- Export d'audit protégé.

## 40. Scénarios de validation

- Caissier tente une remise interdite.
- Comptable tente d'accéder à une autre entreprise.
- Comptable en chef accède uniquement aux entreprises autorisées.
- Utilisateur tente une action après révocation.
- Remboursement au-dessus du seuil.
- Ajustement stock important.
- Réouverture d'une période clôturée.
- Modification d'un fournisseur sensible.
- Double approbation.
- Transaction offline dupliquée.
- Webhook répété.
- Export hors scope.
- Compte compromis.
- Restauration d'une sauvegarde.
- Détection d'une activité anormale.

## 41. Critères de sortie

- IAM défini.
- MFA défini.
- Permissions et scopes définis.
- Séparation des responsabilités définie.
- Audit complet défini.
- Anti-fraude défini.
- Approbations définies.
- Protection données définie.
- Paiements sécurisés définis.
- API sécurisée.
- DB sécurisée.
- Offline sécurisé.
- Exports sécurisés.
- Sauvegardes sécurisées.
- Gestion incidents définie.
- Conformité gouvernée.
- Tests de sécurité et anti-fraude définis.

## 42. Étape suivante

Passer à la **Phase 12 — Intégrations, Paiements & Écosystème**. Elle détaillera les
connexions avec banques, Mobile Money, terminaux de paiement, imprimantes, scanners,
balances, e-commerce, messagerie, API externes et autres services nécessaires à l'écosystème
NEXORA.

---

**NEXORA — Phase 11 validée → Phase 12 : Intégrations, Paiements & Écosystème**
