# NEXORA — PHASE 32 — CONFIGURATION & ADAPTABILITÉ

**Moteur universel de personnalisation par entreprise et secteur — NEXORA V1.0**
**Version 1.0**

## 1. Objectif

Permettre à une même instance de NEXORA de s'adapter à une petite entreprise, un supermarché, une pharmacie, un restaurant, un grossiste, une entreprise de services ou un groupe multi-entreprises, sans créer une version logicielle différente. Toutes les fonctionnalités restent disponibles ; chaque entreprise active, masque, configure et ordonne ce dont elle a besoin.

## 2. Principe fondamental

```
MÊME MOTEUR NEXORA
        ↓
CONFIGURATION DE L'ENTREPRISE
        ↓
PROFIL SECTORIEL OPTIONNEL
        ↓
RÈGLES + WORKFLOWS + ÉCRANS
        ↓
EXPÉRIENCE ADAPTÉE
```

- La configuration ne doit pas modifier le code métier.
- Les données restent compatibles même lorsqu'une fonctionnalité est désactivée dans l'interface.
- Toute configuration importante est versionnée et auditée.
- Une entreprise peut changer sa configuration après le lancement.

## 3. Onboarding

```
CRÉER ENTREPRISE
 ↓
TYPE / SECTEUR
 ↓
TAILLE
 ↓
DEVISE / PAYS
 ↓
STRUCTURE
 ↓
FONCTIONNALITÉS
 ↓
RÈGLES
 ↓
UTILISATEURS
 ↓
VALIDATION
 ↓
NEXORA PRÊT
```

## 4. Type d'entreprise

- Commerce de détail.
- Supermarché.
- Pharmacie.
- Restaurant.
- Grossiste.
- Distributeur.
- Entreprise de services.
- Fabrication légère lorsque les modules le permettent.
- Association/organisation selon besoins.
- Autre.

Le type sert principalement à proposer une configuration initiale. Il ne verrouille pas l'entreprise dans une version spécialisée.

## 5. Taille entreprise

- Très petite.
- Petite.
- Moyenne.
- Grande.
- Groupe.
- La taille peut influencer les suggestions de configuration, limites, dashboards et workflows, sans retirer les capacités disponibles.

## 6. Profil sectoriel

- Profil pharmacie.
- Profil restaurant.
- Profil supermarché.
- Profil grossiste.
- Profil services.
- Profil personnalisé.

## 7. Configuration sans code

- Formulaires.
- Interrupteurs.
- Listes.
- Glisser-déposer lorsque pertinent.
- Constructeur de workflows.
- Constructeur de champs.
- Constructeur de documents.
- Constructeur de dashboards.
- Règles conditionnelles.

## 8. Fonctionnalités

- Activer.
- Désactiver dans l'interface.
- Rendre obligatoire.
- Rendre optionnel.
- Définir le rôle autorisé.
- Définir le périmètre.
- Configurer les paramètres.
- Définir les seuils.

Désactiver une fonctionnalité de l'interface ne doit pas supprimer les données historiques associées.

## 9. Modules

```
VENTES
ACHATS
STOCK
CLIENTS
FOURNISSEURS
FINANCE
COMPTABILITÉ
FACTURATION
REPORTING
...
      ↓
CONFIGURATION PAR ENTREPRISE
```

- POS.
- Crédit.
- Lots.
- Expiration.
- Restaurant.
- Livraison.
- Budgets.
- Analytique.
- Immobilisations.
- Consolidation.
- Etc.

## 10. Menus et navigation

- Menu par rôle.
- Ordre configurable.
- Sections.
- Favoris.
- Raccourcis.
- Masquage fonctionnel.
- Dashboard par défaut.

## 11. Champs personnalisés

- Client.
- Fournisseur.
- Produit.
- Vente.
- Commande.
- Facture.
- Employé/utilisateur.
- Dépense.
- Actif.
- Projet.
- Champ texte.
- Nombre.
- Date.
- Choix.
- Booléen.
- Référence.

## 12. Champs conditionnels

```
SI TYPE = PHARMACIE
→ AFFICHER LOT / EXPIRATION

SI TYPE = RESTAURANT
→ AFFICHER TABLE / SERVICE

SI TYPE = GROSSISTE
→ AFFICHER CARTON / PALETTE
```

- Conditions composables.
- Validation.
- Valeur par défaut.
- Obligatoire conditionnel.
- Audit de configuration.

## 13. Unités

- Pièce.
- Boîte.
- Carton.
- Kg.
- g.
- L.
- ml.
- M.
- Service.
- Heure.
- Unité personnalisée.
- Conversion entre unités.

## 14. Catégories

- Hiérarchie.
- Sous-catégories.
- Ordre.
- Taxes.
- Comptes.
- Entrepôts.
- Produits.
- Attributs.
- Configuration par entreprise.

## 15. Produits configurables

- Produit stocké.
- Service.
- Produit pesé.
- Produit composé.
- Produit avec lots.
- Produit avec expiration.
- Produit sérialisé.
- Produit non stocké.
- Kit/bundle selon module.

## 16. Prix

- Prix standard.
- Prix détail.
- Prix gros.
- Prix client.
- Prix par quantité.
- Prix par établissement.
- Prix promotionnel.
- Devise.
- Arrondi.
- Marge minimale.

## 17. Taxes

- Régime fiscal.
- Taux.
- Exonération.
- Taxe incluse/exclue.
- Règles par catégorie.
- Compte taxe.
- Configuration par entreprise.

## 18. Devises

- Devise fonctionnelle.
- Devises autorisées.
- Devise groupe.
- Taux.
- Source taux.
- Arrondi.
- Règles de change.

## 19. Numérotation

- Factures.
- Avoirs.
- Devis.
- Commandes.
- Reçus.
- Bons.
- Journaux.
- Lots.
- Clients.
- Fournisseurs.
- Séries par entreprise/établissement.

## 20. Workflow configurable

```
DOCUMENT / ACTION
      ↓
CONDITION
      ↓
APPROBATEUR
      ↓
SEUIL
      ↓
ACTION SUIVANTE
```

- Demande d'achat.
- Commande.
- Remise.
- Crédit client.
- Remboursement.
- Dépense.
- Paiement fournisseur.
- Radiation.
- Clôture.

## 21. Moteur de règles

- SI montant > seuil.
- SI client bloqué.
- SI stock < minimum.
- SI marge < minimum.
- SI remise > limite.
- SI paiement externe en attente.
- SI entreprise spécifique.
- SI rôle spécifique.

## 22. Approbations

- Un niveau.
- Plusieurs niveaux.
- Séquentiel.
- Parallèle.
- Délégation.
- Substitution contrôlée.
- SLA.
- Escalade.
- Audit.

## 23. Rôles

- Rôle système.
- Rôle personnalisé.
- Permissions granulaires.
- Scope entreprise.
- Scope établissement.
- Scope module.
- Scope données.
- Actions sensibles.

## 24. Périmètres d'accès

```
UTILISATEUR
 ├── GROUPE
 ├── ENTREPRISE
 ├── ÉTABLISSEMENT
 ├── DÉPARTEMENT
 └── CAISSE / ENTREPÔT
```

- Accès une entreprise.
- Plusieurs entreprises.
- Groupe complet selon autorisation.
- Lecture seule.
- Écriture.
- Validation.
- Administration.

## 25. Comptable en chef

- Accès à plusieurs entreprises autorisées.
- Vue comptable consolidée.
- Rapports groupe.
- Clôtures selon permission.
- Aucune élévation automatique sur les autres modules.

## 26. Configuration POS

- Écran.
- Taille boutons.
- Catégories.
- Favoris.
- Recherche.
- Scanner.
- Impression.
- Paiements.
- Remises.
- Crédit.
- Raccourcis.
- Affichage prix.

## 27. Configuration restaurant

- Tables.
- Zones.
- Services.
- Menus.
- Options.
- Modificateurs.
- Cuisine.
- Imprimantes.
- Pourboire.
- Partage addition.
- Taxes/service.

## 28. Configuration pharmacie

- Lots.
- Expiration.
- FEFO.
- Quarantaine.
- Traçabilité.
- Alertes expiration.
- Contrôles retour.
- Champs spécifiques.

## 29. Configuration supermarché

- Rayons.
- PLU.
- Balance.
- Produits pondérés.
- Scanner.
- Caisses.
- Promotions.
- Étiquettes.
- Prix par poids.

## 30. Configuration grossiste

- Tarifs quantité.
- Conditionnements.
- Palettes.
- Cartons.
- MOQ.
- Clients professionnels.
- Livraison.
- Crédit.

## 31. Configuration services

- Catalogue services.
- Tarifs.
- Unités temps.
- Prestataires.
- Projets.
- Facturation périodique lorsque module disponible.
- Commissions.

## 32. Configuration comptable

- Plan comptable.
- Journaux.
- Mapping produits.
- Mapping taxes.
- Mapping paiements.
- Mapping stock.
- Axes analytiques.
- Devise.
- Exercice.
- Règles de clôture.

## 33. Configuration financière

- Comptes caisse.
- Banques.
- Mobile Money.
- Budgets.
- Seuils.
- Approbations.
- Catégories dépenses.
- Méthodes rapprochement.

## 34. Configuration stock

- Entrepôts.
- Emplacements.
- Min/max.
- Méthode valorisation.
- Lots.
- Expiration.
- FEFO.
- Inventaires.
- Transferts.
- Réservations.

## 35. Configuration clients

- Conditions crédit.
- Limites.
- Échéances.
- Relances.
- Blocage.
- Aging.
- Risque.
- Affectation commercial.

## 36. Configuration fournisseurs

- Conditions paiement.
- Workflow approbation.
- 3-way match.
- Limites.
- Blocage.
- Performance.
- Réapprovisionnement.

## 37. Configuration documents

- Templates.
- Logo.
- Couleurs.
- Langue.
- Formats.
- Mentions.
- Signatures.
- QR.
- Numérotation.
- Email.

## 38. Configuration reporting

- Dashboard par rôle.
- KPI.
- Objectifs.
- Seuils.
- Rapports planifiés.
- Exports.
- Partage.
- Filtres par défaut.

## 39. Configuration notifications

- Canal.
- Événement.
- Destinataire.
- Priorité.
- Horaires.
- Modèle.
- Escalade.
- Silence temporaire.

## 40. Local vs cloud

- Installation locale pour petite entreprise.
- Déploiement cloud.
- Déploiement hybride.
- Synchronisation contrôlée.
- Même logique métier.
- Migration vers cloud sans changer le modèle fonctionnel.

## 41. Mode local

```
NEXORA LOCAL
 ├── POS
 ├── STOCK
 ├── FINANCE
 ├── COMPTABILITÉ
 └── BASE LOCALE
```

- Fonctionnement sans Internet selon capacités.
- Sauvegardes locales.
- Utilisateurs locaux.
- Synchronisation optionnelle.

## 42. Mode cloud

```
NEXORA CLOUD
 ↓
ENTREPRISES
 ↓
UTILISATEURS
 ↓
DASHBOARDS
 ↓
SYNCHRONISATION TEMPS RÉEL
```

## 43. Mode hybride

- Caisse locale.
- Serveur central.
- Outbox/inbox.
- Synchronisation.
- Résolution conflits.
- Journal de synchronisation.
- Reprise après panne.

## 44. Configuration versionnée

- Version.
- Auteur.
- Date.
- Changements.
- Validation.
- Activation.
- Rollback lorsque sûr.
- Audit.

## 45. Environnements

- Configuration brouillon.
- Test/simulation.
- Production.
- Publication contrôlée.
- Historique.

## 46. Import configuration

- Modèle secteur.
- Import JSON/CSV selon besoin.
- Validation.
- Prévisualisation.
- Erreurs.
- Publication.

## 47. Export configuration

- Sauvegarde.
- Clonage entreprise.
- Modèle groupe.
- Migration.
- Audit.
- Secrets exclus.

## 48. Clonage

- Cloner structure.
- Cloner catégories.
- Cloner règles.
- Cloner workflows.
- Cloner templates.
- Ne jamais cloner secrets, soldes ou historiques par défaut.

## 49. Marketplace interne de modèles

- Modèle pharmacie.
- Modèle restaurant.
- Modèle commerce.
- Modèle grossiste.
- Modèle services.
- Modèle personnalisé.
- Version du modèle.
- Import contrôlé.

## 50. Assistant de configuration

```
QUESTIONS
 ↓
RECOMMANDATIONS
 ↓
CONFIGURATION
 ↓
VÉRIFICATION
 ↓
PUBLICATION
```

- Détection de besoins.
- Avertissements.
- Configuration minimale.
- Checklist.

## 51. Simulation

- Tester une règle avant activation.
- Simuler workflow.
- Simuler taxe.
- Simuler numérotation.
- Simuler approbation.
- Voir impact sans modifier production.

## 52. Garde-fous

- Empêcher configuration incohérente.
- Compte comptable obligatoire lorsque requis.
- Devise valide.
- Séquence unique.
- Workflow sans boucle.
- Approbateur autorisé.
- Taxe compatible.
- Configuration critique validée.

## 53. Audit

- Qui a changé quoi.
- Ancienne valeur.
- Nouvelle valeur.
- Date.
- Entreprise.
- Contexte.
- Publication.
- Rollback.
- Approbation.

## 54. API principales

```
GET/POST /api/v1/company-settings
GET/POST /api/v1/module-settings
GET/POST /api/v1/custom-fields
GET/POST /api/v1/workflows
GET/POST /api/v1/rules
GET/POST /api/v1/approval-policies
GET/POST /api/v1/document-templates
GET/POST /api/v1/numbering-series
GET/POST /api/v1/sector-profiles
POST     /api/v1/configurations/{id}/validate
POST     /api/v1/configurations/{id}/publish
POST     /api/v1/configurations/{id}/rollback
GET      /api/v1/configurations/{id}/history
```

## 55. Événements

```
ConfigurationDrafted
ConfigurationValidated
ConfigurationPublished
ConfigurationRolledBack
WorkflowPublished
RulePublished
RoleUpdated
CustomFieldPublished
SectorProfileApplied
```

## 56. Tests

- Création entreprise.
- Profil pharmacie.
- Profil restaurant.
- Profil supermarché.
- Profil grossiste.
- Profil services.
- Activation module.
- Masquage interface.
- Champ conditionnel.
- Workflow.
- Seuil.
- Approbation.
- Rôle personnalisé.
- Scope multi-entreprises.
- Numérotation.
- Taxes.
- Mapping comptable.
- Clonage.
- Import/export.
- Publication.
- Rollback.
- Local.
- Cloud.
- Hybride.
- Isolation.

## 57. Scénario

```
ENTREPRISE X
TYPE = RESTAURANT
 ↓
ACTIVE TABLES + CUISINE + POURBOIRE
 ↓
CONSERVE STOCK + POS + FINANCE + COMPTABILITÉ
 ↓
AJOUTE LIVRAISON
 ↓
CHANGE DE DEVISE
 ↓
AJOUTE UNE DEUXIÈME SUCCURSALE
 ↓
CONFIGURATION MISE À JOUR
SANS CHANGER DE VERSION NEXORA
```

## 58. Critères de sortie

- Onboarding.
- Types entreprise.
- Profils secteur.
- Modules configurables.
- Menus.
- Champs personnalisés.
- Règles.
- Workflows.
- Approbations.
- Rôles.
- Scopes.
- POS configurable.
- Restaurant.
- Pharmacie.
- Supermarché.
- Grossiste.
- Services.
- Comptabilité configurable.
- Finance configurable.
- Stock configurable.
- Clients/fournisseurs.
- Documents.
- Reporting.
- Notifications.
- Local.
- Cloud.
- Hybride.
- Versioning.
- Import/export.
- Clonage.
- Modèles.
- Simulation.
- Garde-fous.
- Audit.
- API.
- Tests.

## 59. Étape suivante

Passer à la Phase 33 — Utilisateurs, sécurité, authentification, rôles, permissions, MFA, sessions, appareils, audit de sécurité et gouvernance des accès. Cette phase renforcera le cloisonnement indispensable à une plateforme multi-entreprises.

---

**NEXORA V1.0 — PHASE 32 VALIDÉE → PHASE 33 : IDENTITÉ, SÉCURITÉ & ACCÈS**
