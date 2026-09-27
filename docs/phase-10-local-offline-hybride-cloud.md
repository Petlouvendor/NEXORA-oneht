# NEXORA — PHASE 10 — LOCAL, OFFLINE, HYBRIDE & CLOUD

**Architecture de déploiement et continuité d'activité**
Version 1.0

## 1. Objectif

Permettre à NEXORA de fonctionner avec le même produit dans des contextes très différents :
petite entreprise sur un ordinateur ou réseau local, entreprise multi-caisses sur serveur
local, entreprise multi-sites, cloud, ou architecture hybride avec continuité temporaire
hors connexion.

## 2. Principe directeur

Le mode de déploiement ne doit pas changer les règles métier. NEXORA doit conserver le même
noyau de ventes, stocks, finance, comptabilité, utilisateurs et sécurité ; seuls
l'infrastructure et le mode de synchronisation changent.

## 3. Modes supportés

- Local mono-poste.
- Local réseau.
- Serveur local + plusieurs terminaux.
- Cloud centralisé.
- Hybride site local + cloud.
- Offline temporaire sur POS.
- Multi-sites connectés.

## 4. Mode local mono-poste

```
APPLICATION NEXORA → POSTGRESQL LOCAL
```

- Adapté à une très petite entreprise.
- Un ordinateur peut héberger application et base.
- Fonctionnement sans Internet.
- Sauvegarde locale.
- Imprimante et périphériques locaux.
- Possibilité de migration ultérieure vers serveur/cloud.

## 5. Mode local réseau

```
POSTES POS / WEB
       ↓ LAN
SERVEUR NEXORA
       ↓
POSTGRESQL LOCAL
```

- Plusieurs caisses.
- Plusieurs utilisateurs.
- Réseau interne.
- Données centralisées.
- Internet non obligatoire pour les opérations locales.
- Sauvegarde vers support distinct recommandé.

## 6. Mode cloud

```
POS / WEB
   ↓ HTTPS
CLOUD NEXORA
   ↓
POSTGRESQL
   ↓
BACKUP / MONITORING
```

- Accès distant.
- Multi-sites.
- Administration centralisée.
- Sauvegardes automatisées.
- Haute disponibilité selon infrastructure choisie.
- Mises à jour centralisées.

## 7. Mode hybride

```
SITE A                         CLOUD
POS → API LOCAL → SYNC ↔ API CENTRAL
         ↓                         ↓
       DB LOCAL                 DB CENTRAL

SITE B → API LOCAL → SYNC ↔ API CENTRAL
```

- Chaque site peut conserver un service local.
- Les données centrales permettent la vision groupe.
- Synchronisation contrôlée.
- Continuité locale si Internet indisponible.
- Les règles de conflit sont explicites.

## 8. Pourquoi l'offline est essentiel

- Coupure Internet.
- Réseau instable.
- Panne temporaire du fournisseur.
- Sites éloignés.
- Opérations de caisse qui ne doivent pas s'arrêter.
- Continuité des ventes et consultation locale.

L'offline n'est pas une copie complète et incontrôlée du système : seules les fonctions et
données nécessaires sont disponibles localement.

## 9. Fonctions offline prioritaires

- Connexion utilisateur locale sécurisée.
- Catalogue disponible.
- Recherche produit.
- Panier.
- Calcul prix/taxes configurés.
- Vente selon politique offline.
- Impression du reçu.
- Paiements autorisés offline.
- Création d'un identifiant unique.
- Mise en file pour synchronisation.
- Consultation des ventes locales.
- État de synchronisation.

## 10. Fonctions pouvant exiger le serveur

- Validation de certains crédits.
- Dépassement de limite de crédit.
- Actions financières sensibles.
- Changement de permissions.
- Consolidation.
- Administration groupe.
- Certaines intégrations de paiement en ligne.
- Opérations réglementées nécessitant une vérification distante.

La politique doit être configurable : chaque entreprise définit ce qui peut être réalisé
hors connexion dans les limites de sécurité.

## 11. Architecture offline

```
UTILISATEUR
   ↓
POS LOCAL
   ↓
LOCAL STORE / QUEUE
   ↓
SYNC ENGINE
   ↓ Internet disponible
API CENTRAL
   ↓
POSTGRESQL CENTRAL
```

- Stockage local sécurisé.
- Queue durable.
- Identifiant global de transaction.
- État pending/sent/confirmed/failed.
- Retry automatique.
- Backoff.
- Accusé de réception.
- Journal de synchronisation.

## 12. Identifiant de transaction

Chaque opération créée offline doit posséder un identifiant global unique permettant de la
reconnaître lors de la synchronisation et d'éviter les doublons.

```
TRANSACTION_ID = UNIQUE GLOBAL
DEVICE_ID + LOCAL_SEQUENCE + IDENTIFIANT TEMPOREL / UUID
```

## 13. Idempotence

Si une transaction est envoyée deux fois, le serveur doit reconnaître qu'il s'agit de la
même opération et ne pas créer une seconde vente, un second paiement ou un second mouvement
de stock.

- Clé d'idempotence.
- Détection côté serveur.
- Réponse cohérente en cas de répétition.
- Journal des tentatives.

## 14. Synchronisation

```
LOCAL PENDING
     ↓
VALIDATION FORMAT
     ↓
AUTHENTIFICATION
     ↓
ENVOI
     ↓
SERVEUR
 ┌── SUCCÈS → CONFIRMED
 ├── RETRY → PENDING
 └── CONFLIT → CONFLICT
```

- Synchronisation automatique.
- Synchronisation manuelle.
- Priorité aux transactions critiques.
- File persistante.
- Reprise après redémarrage.
- Rapport des erreurs.
- Historique.

## 15. Gestion des conflits

- Définir les types de conflits.
- Ne jamais écraser silencieusement une donnée.
- Prioriser la source autoritaire selon le domaine.
- Créer une tâche de résolution pour les conflits métier.
- Journaliser décision et responsable.
- Empêcher la double consommation de stock.

## 16. Conflit de stock

Exemple : deux sites ou caisses tentent de vendre le dernier article.

```
STOCK CENTRAL = 1
SITE A OFFLINE → VENTE 1
SITE B OFFLINE → VENTE 1
          ↓
CONFLIT À LA SYNCHRONISATION
```

- La politique doit définir le comportement.
- Le système doit détecter l'incohérence.
- Ne jamais masquer le conflit.
- Créer une alerte et une procédure de correction.
- Les rapports doivent distinguer vente confirmée localement et stock central réconcilié
  lorsque nécessaire.

## 17. Conflit de crédit client

- Le crédit disponible localement doit être basé sur le dernier état connu.
- Vente offline à crédit limitée selon politique.
- Dépassement éventuel signalé après synchronisation.
- Possibilité d'interdire totalement le crédit offline.
- Audit obligatoire.

## 18. Paiements offline

- Espèces : généralement possible.
- Paiements intégrés : dépend du terminal/fournisseur.
- Carte : dépend du terminal et de son mode offline.
- Mobile Money : dépend du fournisseur.
- Chaque paiement conserve son identifiant et statut.
- Un paiement non confirmé ne doit pas être présenté comme définitivement encaissé si le
  fournisseur ne l'a pas confirmé.

## 19. Réseau local et sécurité

- Réseau privé.
- Pare-feu.
- TLS lorsque pertinent même sur réseau interne.
- Comptes individuels.
- Pas de compte POS partagé.
- Verrouillage automatique.
- Journalisation.
- Sauvegarde serveur.

## 20. Sauvegardes locales

- Backup PostgreSQL.
- Fréquence configurable.
- Copie sur support différent.
- Rotation.
- Chiffrement.
- Test de restauration.
- Sauvegarde des fichiers/documents.
- Procédure de restauration documentée.

## 21. Sauvegardes cloud

- Automatisation.
- Rétention.
- Copies redondantes.
- Chiffrement.
- Tests de restauration.
- Surveillance des échecs.
- Objectifs RPO/RTO adaptés au niveau de service.

## 22. Mise à jour

- Version du serveur.
- Version des clients.
- Migrations DB.
- Compatibilité temporaire si nécessaire.
- Déploiement progressif.
- Rollback.
- Journal de version.
- Possibilité de maintenance planifiée.

## 23. Installation petite entreprise

```
INSTALLER NEXORA
→ CONFIGURER ENTREPRISE
→ CONFIGURER UTILISATEURS
→ IMPORTER PRODUITS
→ CONFIGURER CAISSE
→ CONFIGURER FINANCE/COMPTABILITÉ
→ SAUVEGARDER
→ COMMENCER
```

## 24. Migration local → cloud

- Exporter/backup.
- Vérifier intégrité.
- Importer dans environnement cloud.
- Recalculer contrôles.
- Vérifier soldes.
- Vérifier stock.
- Vérifier créances/dettes.
- Tester utilisateurs.
- Basculer les terminaux.
- Conserver sauvegarde source.

## 25. Migration cloud → local

- Export autorisé.
- Backup.
- Import.
- Validation.
- Configuration réseau.
- Tests POS.
- Vérification des soldes.
- Plan de reprise.
- Désactivation contrôlée de l'ancien environnement.

## 26. Multi-sites

- Identifiant site.
- Stock local.
- Caisse locale.
- Utilisateurs locaux.
- Données centrales.
- Synchronisation.
- Transferts inter-sites.
- Rapports site.
- Rapports groupe.

## 27. Continuité d'activité

```
PANNE INTERNET
      ↓
MODE OFFLINE
      ↓
VENTES LOCALES
      ↓
QUEUE
      ↓ Internet revient
SYNCHRONISATION
      ↓
RAPPROCHEMENT
      ↓
CONFIRMATION
```

- Indicateur visible.
- Temps offline affiché.
- Nombre de transactions en attente.
- Alertes après seuil.
- Procédure opérateur.

## 28. Mode dégradé

- Si API centrale indisponible mais réseau local disponible, continuer selon politique.
- Si base locale indisponible, arrêter les opérations nécessitant une écriture fiable plutôt
  que simuler des ventes non persistées.
- Afficher clairement l'état système.
- Ne jamais inventer une confirmation.

## 29. Monitoring

- Disponibilité API.
- État PostgreSQL.
- CPU/mémoire/disque.
- Espace disque.
- État synchronisation.
- Nombre d'opérations en attente.
- Échecs de synchronisation.
- Temps depuis dernière synchronisation.
- État sauvegarde.
- Certificats/TLS lorsque applicable.

## 30. Centre de synchronisation

- Dernière synchronisation.
- Transactions en attente.
- Transactions réussies.
- Échecs.
- Conflits.
- Actions recommandées.
- Forcer synchronisation.
- Exporter rapport.
- Historique.

## 31. Sécurité du mode offline

- Données locales chiffrées lorsque nécessaire.
- Expiration des sessions.
- Révocation des appareils.
- Limitation des données sensibles.
- Protection contre copie non autorisée.
- Identité appareil.
- Journal des opérations.
- Effacement contrôlé des données locales après synchronisation selon politique.

## 32. Périphériques POS

- Imprimante thermique.
- Scanner code-barres.
- Balance.
- Tiroir-caisse.
- Terminal de paiement.
- Afficheur client.
- Écran tactile.
- Lecteurs spécialisés selon secteur.

Les périphériques doivent être encapsulés derrière une couche d'intégration afin que le cœur
métier reste indépendant du matériel.

## 33. Déploiement recommandé par taille

| Taille | Architecture | Internet | Évolution |
|---|---|---|---|
| Très petite | Mono-poste local | Optionnel | Cloud possible |
| Petite | Serveur local + POS | Optionnel | Hybride/cloud |
| Moyenne | Serveur local ou cloud | Recommandé | Multi-sites |
| Grande | Cloud/hybride | Recommandé | Haute disponibilité |
| Groupe | Cloud/hybride multi-sites | Recommandé | Consolidation |

## 34. Critères de choix

- Nombre de caisses.
- Nombre d'utilisateurs.
- Nombre de sites.
- Fiabilité Internet.
- Besoin de mobilité.
- Volume de transactions.
- Sensibilité des données.
- Budget infrastructure.
- Besoin de consolidation.
- Niveau de continuité requis.

## 35. Scénarios de validation

- Installer une entreprise mono-poste sans Internet.
- Faire plusieurs ventes sur réseau local.
- Débrancher Internet pendant une session POS.
- Créer plusieurs ventes offline.
- Rétablir Internet et synchroniser.
- Envoyer volontairement deux fois la même transaction.
- Créer un conflit de stock.
- Créer un conflit de crédit.
- Vérifier les paiements offline selon politique.
- Restaurer une sauvegarde.
- Migrer local vers cloud.
- Ajouter un second site hybride.
- Vérifier le centre de synchronisation.
- Révoquer un appareil offline.
- Vérifier qu'une opération non persistée n'est jamais confirmée comme vente.

## 36. Critères de sortie

- Modes local, cloud et hybride définis.
- Offline défini.
- Queue et synchronisation définies.
- Identifiants et idempotence définis.
- Conflits définis.
- Paiements offline définis.
- Sauvegardes définies.
- Restauration définie.
- Migrations définies.
- Multi-sites défini.
- Monitoring défini.
- Centre de synchronisation défini.
- Sécurité offline définie.
- Périphériques définis.
- Scénarios testables.

## 37. Étape suivante

Passer à la **Phase 11 — Sécurité, Audit, Conformité & Anti-fraude**. Elle consolidera la
protection de NEXORA : accès, audit immuable, contrôles financiers, prévention des abus,
traçabilité, protection des données et préparation aux exigences des entreprises haïtiennes
et internationales.

---

**NEXORA — Phase 10 validée → Phase 11 : Sécurité, Audit & Conformité**
