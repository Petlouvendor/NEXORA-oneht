# NEXORA — PHASE 14 — ADMINISTRATION, PARAMÉTRAGE & AUTOMATISATION

**Centre de configuration permettant d'adapter NEXORA sans modifier le cœur métier**
Version 1.0

## 1. Objectif

Donner aux administrateurs les outils nécessaires pour configurer NEXORA selon la taille, le
secteur et l'organisation de chaque entreprise, sans développement spécifique pour les
paramètres courants. Le système doit permettre une évolution progressive d'une petite entreprise
vers une organisation multi-sites ou un groupe.

## 2. Principe directeur

- Configuration sans code pour les besoins standards.
- Chaque paramètre possède un périmètre.
- Les paramètres globaux ne doivent pas écraser les paramètres locaux sans règle claire.
- Les changements sensibles sont audités.
- Les paramètres financiers ont des contrôles renforcés.
- Une configuration future ne réécrit pas l'historique.
- Les automatisations doivent être explicables et désactivables.
- Le système doit distinguer configuration, données et transactions.

## 3. Hiérarchie de configuration

```
NEXORA
├── GROUPE
│   ├── ENTREPRISE
│   │   ├── ÉTABLISSEMENT
│   │   │   ├── MAGASIN / ENTREPÔT
│   │   │   └── CAISSE
│   │   └── UTILISATEURS
│   └── POLITIQUES GROUPE
└── SYSTÈME
```

Chaque niveau ne peut modifier que les paramètres qui lui appartiennent.

## 4. Centre d'administration

- Vue générale.
- Configuration entreprise.
- Utilisateurs et rôles.
- Organisation.
- Produits et stock.
- Ventes/POS.
- Achats.
- Finance.
- Comptabilité.
- Taxes.
- Documents.
- Notifications.
- Automatisations.
- Intégrations.
- Sécurité.
- Audit.
- Sauvegarde.
- Import/export.
- Paramètres avancés.

## 5. Assistant de configuration initiale

```
1. GROUPE OU ENTREPRISE INDÉPENDANTE
2. ACTIVITÉ(S)
3. INFORMATIONS LÉGALES
4. DEVISE / PAYS / FUSEAU
5. TAXES
6. COMPTABILITÉ
7. STOCK
8. POS
9. UTILISATEURS
10. DOCUMENTS
11. PAIEMENTS
12. SAUVEGARDE
13. TEST
14. ACTIVATION
```

- Progression.
- Valeurs par défaut.
- Vérification des champs.
- Résumé avant validation.
- Possibilité de reprendre plus tard.
- Checklist finale.

## 6. Profil d'entreprise

- Nom.
- Logo.
- Coordonnées.
- Adresses.
- Identifiants légaux selon besoin.
- Secteur principal.
- Activités secondaires.
- Devise.
- Fuseau horaire.
- Format dates.
- Format nombres.
- Informations affichées sur documents.

## 7. Paramétrage des établissements

- Créer/modifier établissement.
- Adresse.
- Horaires.
- Caisse(s).
- Entrepôt(s).
- Responsables.
- Taxes locales lorsque pertinent.
- Paramètres POS.
- Paramètres stock.
- Paramètres de documents.

## 8. Paramétrage caisses

- Nom caisse.
- Terminal.
- Utilisateur(s) autorisé(s).
- Fond initial par politique.
- Moyens de paiement.
- Imprimante.
- Scanner.
- Balance.
- Tiroir.
- Format ticket.
- Limites.
- Mode offline.

## 9. Utilisateurs

- Créer utilisateur.
- Modifier profil.
- Activer/désactiver.
- Réinitialiser accès.
- Affecter entreprise.
- Affecter établissement.
- Affecter caisse.
- Affecter rôle.
- Définir permissions supplémentaires.
- Révoquer appareil.

## 10. Rôles personnalisés

- Créer rôle.
- Dupliquer rôle existant.
- Choisir permissions.
- Définir scope.
- Prévisualiser accès.
- Tester rôle.
- Versionner ou historiser les changements.
- Désactiver rôle sans supprimer l'historique.

## 11. Matrice des permissions

```
RESSOURCE × ACTION × SCOPE

VENTE × VOIR × ENTREPRISE A
VENTE × REMBOURSER × CAISSE 2
COMPTABILITÉ × CLÔTURER × ENTREPRISE B
CONSOLIDATION × PUBLIER × GROUPE
```

- Voir.
- Créer.
- Modifier.
- Valider.
- Annuler.
- Rembourser.
- Exporter.
- Configurer.
- Administrer.

## 12. Configuration des produits

- Catégories.
- Marques.
- Unités.
- Conversions.
- Attributs.
- Champs personnalisés.
- Taxes.
- Prix.
- Promotions.
- Seuils stock.
- Lots.
- Expiration.
- Règles sectorielles.

## 13. Champs personnalisés

- Texte.
- Nombre.
- Date.
- Liste.
- Booléen.
- Référence.
- Fichier.
- Obligatoire.
- Valeur par défaut.
- Visibilité.
- Validation.
- Périmètre.

L'administrateur peut adapter le référentiel à une activité particulière sans modifier le
modèle de données central.

## 14. Workflows configurables

```
ÉVÉNEMENT → CONDITION → APPROBATION → ACTION → NOTIFICATION
```

- Créer workflow.
- Définir déclencheur.
- Conditions.
- Seuils.
- Rôles approbateurs.
- Actions.
- Délais.
- Escalade.
- Notifications.
- Historique.

## 15. Exemples de workflows

- Dépense > seuil → approbation direction.
- Remise > seuil → approbation manager.
- Ajustement stock important → double validation.
- Nouvelle dette fournisseur → contrôle.
- Dépassement crédit → autorisation.
- Réouverture période → comptable en chef.
- Nouvel utilisateur financier → approbation.

## 16. Moteur de règles

- Conditions sur montant.
- Produit.
- Catégorie.
- Client.
- Fournisseur.
- Entreprise.
- Établissement.
- Utilisateur.
- Horaire.
- Statut.
- Historique.
- Risque.

```
IF condition THEN action
```

Les règles doivent être testables avant activation et afficher clairement leurs effets.

## 17. Automatisations

- Réapprovisionnement.
- Alertes stock.
- Relances créances.
- Rappels fournisseurs.
- Rapports planifiés.
- Notifications.
- Création de tâches.
- Synchronisation.
- Rapprochements assistés.
- Archivage selon politique.
- Exports planifiés.

## 18. Planificateur

- Une fois.
- Horaire.
- Quotidien.
- Hebdomadaire.
- Mensuel.
- Date de début.
- Date de fin.
- Fuseau horaire.
- Conditions.
- Historique d'exécution.
- Échec/retry.

## 19. Relances clients

- Échéance proche.
- Échéance dépassée.
- Niveau 1/2/3.
- Message configurable.
- Canal.
- Responsable.
- Historique.
- Désactivation par client si nécessaire.
- Respect des permissions.

## 20. Alertes configurables

- Stock faible.
- Expiration.
- Créance échue.
- Dette échue.
- Écart caisse.
- Marge faible.
- Dépense inhabituelle.
- Synchronisation en échec.
- Sauvegarde en échec.
- Intégration indisponible.

## 21. Taxes

- Créer taux.
- Catégories fiscales.
- Exonération.
- Règles par produit.
- Règles par établissement si applicable.
- Période de validité.
- Historique.
- Contrôle des modifications.

Les règles fiscales doivent être configurées selon le cadre applicable et validées par les
responsables compétents.

## 22. Devise et monnaie

- Devise principale.
- Devises acceptées.
- Taux.
- Source du taux.
- Date d'effet.
- Arrondis.
- Taux manuel autorisé selon permission.
- Devise de consolidation groupe.

## 23. Numérotation

- Vente.
- Facture.
- Avoir.
- Achat.
- Bon commande.
- Bon réception.
- Paiement.
- Écriture.
- Dépense.
- Document interne.
- Préfixe.
- Séquence.
- Période.
- Contrôle des doublons.

Les documents financiers validés doivent suivre une politique de numérotation contrôlée et
historisée.

## 24. Documents

- Modèles de facture.
- Reçu.
- Bon de commande.
- Bon de livraison.
- Avoir.
- Rapports.
- États financiers.
- En-tête.
- Pied de page.
- Logo.
- Mentions.
- QR code.
- Langue.
- Format papier.

## 25. Terminologie

- Libellés personnalisables.
- Noms d'activités.
- Statuts.
- Titres documents.
- Unités.
- Traductions.
- Glossaire comptable.

La personnalisation de vocabulaire ne doit jamais modifier les règles métier sous-jacentes.

## 26. Configuration POS

- Écran d'accueil.
- Catégories rapides.
- Produits favoris.
- Raccourcis.
- Moyens de paiement.
- Limites de remise.
- Crédit.
- Retour.
- Impression.
- Ouverture tiroir.
- Mode offline.
- Vente suspendue.
- Paramètres de session.

## 27. Configuration stock

- Méthode de valorisation.
- Seuil minimum.
- Maximum.
- Point commande.
- Lots.
- Expiration.
- FEFO.
- Inventaire.
- Transferts.
- Emplacements.
- Pertes.
- Ajustements.
- Approbations.

## 28. Configuration achats

- Workflow demande.
- Approbation.
- Fournisseurs.
- Conditions paiement.
- Limites.
- Réception partielle.
- Tolérances quantité/prix.
- Retour.
- Avoir.
- Facture.
- Règles de contrôle.

## 29. Configuration finance

- Caisses.
- Banques.
- Comptes financiers.
- Moyens paiement.
- Budgets.
- Centres de coûts.
- Seuils.
- Rapprochements.
- Prévisions.
- Catégories dépenses.

## 30. Configuration comptable

- Plan comptable.
- Journaux.
- Périodes.
- Mapping groupe.
- Taxes.
- Comptes par défaut.
- Règles de génération.
- Clôture.
- Réouverture.
- Analytique.
- Intercompany.

Les paramètres comptables sensibles nécessitent des contrôles renforcés et doivent être
compatibles avec les règles comptables retenues par l'entreprise.

## 31. Comptes par défaut

- Ventes.
- Achats.
- Taxes.
- Stock.
- Variation stock.
- Caisse.
- Banque.
- Clients.
- Fournisseurs.
- Remises.
- Pertes.
- Frais.

## 32. Paramétrage par secteur

- Pharmacie : lots, expiration, FEFO, champs réglementaires.
- Restaurant : tables, KDS, recettes, ingrédients.
- Supermarché : poids, balances, promotions, multi-caisses.
- Grossiste : tarifs, livraison, crédit.
- Services : prestations, durée, commissions.
- Production : nomenclatures, ordres, coûts.

## 33. Configuration multi-entreprises

- Créer entreprise.
- Ajouter au groupe.
- Affecter utilisateurs.
- Définir visibilité.
- Définir politiques groupe.
- Configurer consolidation.
- Configurer intercompany.
- Définir devise.
- Mapping comptes.

## 34. Configuration intercompany

- Entreprises partenaires.
- Comptes réciproques.
- Types de transactions.
- Règles de génération.
- Rapprochement.
- Tolérances.
- Éliminations.
- Approbation.

## 35. Configuration des intégrations

- Choisir connecteur.
- Entreprise concernée.
- Environnement test/production.
- Credentials sécurisés.
- Comptes associés.
- Webhooks.
- Paramètres.
- Test connexion.
- Activation.
- Désactivation.
- Historique.

## 36. Configuration offline

- Autoriser offline.
- Durée maximale.
- Ventes autorisées.
- Crédit autorisé ou non.
- Paiements autorisés.
- Limites.
- Données locales.
- Synchronisation.
- Conflits.
- Seuil d'alerte.

## 37. Paramètres de sécurité

- MFA.
- Durée session.
- Verrouillage.
- Politique mot de passe.
- Permissions.
- Approbations.
- IP/réseau selon besoin.
- Appareils.
- Exports.
- Audit.
- Réauthentification.

## 38. Centre d'audit configuration

- Qui a changé quoi.
- Ancienne valeur.
- Nouvelle valeur.
- Date.
- Entreprise.
- Résultat.
- Motif.
- Version configuration.
- Possibilité de consulter l'historique.

## 39. Versionnement de configuration

Les configurations sensibles doivent pouvoir être historisées afin de savoir quelle règle était
active au moment d'une transaction.

```
CONFIG v12 ACTIVE DU 01/08 AU 31/08
CONFIG v13 ACTIVE À PARTIR DU 01/09
```

- Version.
- Date d'effet.
- Auteur.
- Approbateur.
- Différences.
- Rollback lorsque métier et sécurité le permettent.

## 40. Environnement test

- Mode sandbox.
- Tester workflow.
- Tester promotion.
- Tester taxe.
- Tester connecteur.
- Tester imprimante.
- Simuler paiement.
- Prévisualiser document.
- Valider avant production.

## 41. Prévisualisation

- Voir impact avant activation.
- Afficher entreprises concernées.
- Afficher utilisateurs concernés.
- Afficher automatisations touchées.
- Afficher risques.
- Confirmation.

## 42. Import de configuration

- Importer paramètres.
- Valider schéma.
- Prévisualiser changements.
- Détecter conflits.
- Appliquer.
- Rapport.
- Rollback si applicable.

## 43. Export de configuration

- Exporter paramètres non secrets.
- Inclure versions.
- Inclure périmètre.
- Ne jamais exporter secrets en clair.
- Format documenté.
- Audit.

## 44. Centre de tâches administratives

- Configuration incomplète.
- Utilisateurs sans rôle.
- Produits sans taxe.
- Produits sans prix.
- Comptes par défaut manquants.
- Sauvegarde non configurée.
- Intégration en erreur.
- Workflow non testé.
- Clôture en attente.

## 45. Garde-fous

- Empêcher configuration incohérente.
- Empêcher suppression d'un compte utilisé sans procédure.
- Empêcher désactivation d'un moyen de paiement encore utilisé sans alternative.
- Avertir avant modification impactant des transactions futures.
- Bloquer changement incompatible avec historique.
- Exiger approbation pour paramètres critiques.

## 46. Automatisation intelligente

Une couche d'assistance peut proposer des actions, mais les opérations financières critiques ne
doivent pas être exécutées automatiquement sans règles et autorisations explicites.

- Suggestion de réapprovisionnement.
- Suggestion de relance.
- Détection de configuration incomplète.
- Suggestion de workflow.
- Détection de seuil inhabituel.
- Explication de la recommandation.

## 47. Scénarios de validation

- Créer une entreprise depuis l'assistant.
- Ajouter un établissement.
- Créer une caisse.
- Créer un rôle personnalisé.
- Limiter un comptable à une entreprise.
- Créer un champ personnalisé.
- Créer un workflow de remise.
- Créer une automatisation de relance.
- Configurer une taxe.
- Modifier une séquence de documents.
- Configurer POS.
- Configurer FEFO.
- Configurer comptabilité.
- Configurer intercompany.
- Configurer connecteur paiement.
- Configurer offline.
- Modifier un paramètre et vérifier l'audit.
- Prévisualiser une configuration.
- Tester puis publier.
- Vérifier qu'une nouvelle configuration n'altère pas l'historique.

## 48. Critères de sortie

- Centre administration défini.
- Assistant onboarding défini.
- Organisation définie.
- Utilisateurs/rôles définis.
- Permissions définies.
- Champs personnalisés définis.
- Workflows définis.
- Moteur de règles défini.
- Automatisations définies.
- Taxes/devise définies.
- Numérotation définie.
- Documents définis.
- POS/stock/achats/finance/comptabilité configurables.
- Paramètres sectoriels définis.
- Multi-entreprises défini.
- Intégrations/offline configurables.
- Sécurité définie.
- Versionnement défini.
- Sandbox et prévisualisation définis.
- Scénarios testables.

## 49. Étape suivante

Passer à la **Phase 15 — Qualité, Tests, Déploiement & Mise en Production**. Elle définira la
stratégie de tests de NEXORA, les environnements, la validation fonctionnelle, les tests
financiers et comptables, les tests de charge, la migration, la mise en production et les
critères de lancement V1.0.

---

**NEXORA — Phase 14 validée → Phase 15 : Qualité, Tests & Mise en Production**
