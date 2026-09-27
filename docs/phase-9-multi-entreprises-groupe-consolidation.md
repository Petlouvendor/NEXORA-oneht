# NEXORA — PHASE 9 — MULTI-ENTREPRISES, GROUPE & CONSOLIDATION

**Gestion centralisée de plusieurs entreprises avec séparation des données et vision groupe**
Version 1.0

## 1. Objectif

Permettre à une personne, une direction ou un cabinet comptable de gérer plusieurs
entreprises depuis une même installation NEXORA, tout en garantissant l'isolation des
données de chaque entreprise et en offrant une vision groupe lorsque les droits l'autorisent.

## 2. Principe directeur

```
GROUPE
 ├── ENTREPRISE A — Pharmacie
 ├── ENTREPRISE B — Restaurant
 ├── ENTREPRISE C — Supermarché
 └── ENTREPRISE D — Services

DONNÉES INDIVIDUELLES = SÉPARÉES
VISION GROUPE = AUTORISÉE
CONSOLIDATION = PROCESSUS DISTINCT
```

- Une entreprise reste autonome.
- Un utilisateur peut avoir accès à une ou plusieurs entreprises.
- Un rôle peut varier selon l'entreprise.
- Un comptable simple ne voit que son périmètre.
- Un comptable en chef peut superviser plusieurs entreprises autorisées.
- La direction peut avoir une vue groupe.
- La consolidation n'altère jamais les comptabilités sources.

## 3. Structure du groupe

- Groupe légal ou organisationnel.
- Entreprises membres.
- Établissements.
- Magasins.
- Entrepôts.
- Caisses.
- Périmètres comptables.
- Périmètres utilisateurs.

```
GROUP → COMPANIES → ESTABLISHMENTS → OPERATIONS
```

## 4. Entreprise indépendante

NEXORA doit fonctionner sans groupe. Une petite entreprise peut utiliser une installation
contenant une seule entreprise, sans exposer de concepts groupe inutiles à l'utilisateur.

- Une entreprise.
- Un ou plusieurs établissements.
- Une ou plusieurs caisses.
- Comptabilité propre.
- Finance propre.
- Stock propre.
- Utilisateurs propres.

## 5. Entreprise multi-sites

- Plusieurs établissements.
- Stocks par site.
- Caisses par magasin.
- Utilisateurs par site.
- Rapports par site.
- Transferts inter-sites.
- Finance et comptabilité centralisées ou organisées selon configuration.

## 6. Utilisateurs multi-entreprises

L'identité d'un utilisateur est globale, mais ses rôles et accès sont déterminés par son
affectation.

```
USER → AFFECTATION → RÔLE → PERMISSIONS → ENTREPRISE/SCOPE
```

- Un utilisateur peut être comptable de A.
- Le même utilisateur peut être manager de B.
- Il peut n'avoir aucun accès à C.
- Les droits sont indépendants par entreprise.
- Les changements d'affectation sont auditables.

## 7. Matrice des rôles

| Rôle | Entreprise A | Entreprise B | Groupe | Consolidation |
|---|---|---|---|---|
| Caissier | Caisse autorisée | Selon affectation | Non | Non |
| Comptable | Comptabilité autorisée | Selon affectation | Non/limité | Non/selon droit |
| Comptable en chef | Oui | Oui si affecté | Oui | Oui si autorisé |
| Manager | Périmètre autorisé | Périmètre autorisé | Indicateurs si droit | Non |
| Direction | Oui | Oui | Oui | Oui si autorisé |
| Auditeur | Lecture autorisée | Lecture autorisée | Selon scope | Selon droit |

## 8. Sélecteur d'entreprise

- Afficher uniquement les entreprises accessibles.
- Recherche rapide.
- Favoris.
- Dernière entreprise utilisée.
- Indication secteur.
- Indication statut.
- Accès au groupe selon permission.

```
QUICK SWITCH : ENTREPRISE A ↔ ENTREPRISE B ↔ ENTREPRISE C
```

Changer d'entreprise change le contexte d'affichage et de travail ; le serveur doit
néanmoins recalculer et vérifier les autorisations.

## 9. Vue groupe

- Liste des entreprises.
- CA par entreprise.
- Marge par entreprise.
- Résultat.
- Trésorerie.
- Créances.
- Dettes.
- Stock.
- Performance comparative.
- Alertes.
- Clôtures.
- État de synchronisation.

## 10. Comparaison des entreprises

- CA.
- Croissance.
- Marge.
- Bénéfice.
- Dépenses.
- Trésorerie.
- Créances.
- Dettes.
- Stock.
- Panier moyen si comparable.
- Performance par activité.
- Évolution temporelle.

Les indicateurs doivent tenir compte des différences de secteurs et éviter les comparaisons
trompeuses lorsque les métriques ne sont pas comparables.

## 11. Comptabilité multi-entreprises

- Plan comptable propre à chaque entreprise ou plan groupe avec mapping.
- Journaux propres.
- Périodes propres.
- Grand livre propre.
- Balance propre.
- Bilan et résultat propres.
- Clôtures propres.
- Comptables affectés par entreprise.
- Rapports multi-entreprises autorisés.

## 12. Plan comptable groupe

Un groupe peut définir une structure comptable commune ou un référentiel groupe et mapper
les plans locaux vers une structure de consolidation.

```
PLAN LOCAL A ─┐
PLAN LOCAL B ─┼→ MAPPING GROUPE → ÉTATS CONSOLIDÉS
PLAN LOCAL C ─┘
```

- Mapping compte local → compte groupe.
- Gestion des comptes sans équivalent.
- Version du mapping.
- Date d'effet.
- Validation.
- Audit des modifications.

## 13. Devise de consolidation

- Devise fonctionnelle par entreprise.
- Devise de consolidation du groupe.
- Taux de conversion.
- Date/taux utilisé.
- Méthode de conversion selon politique comptable retenue.
- Historisation.
- Différences de conversion.

La conversion de consolidation ne modifie pas les montants historiques des entreprises
sources.

## 14. Transactions intercompany

- Vente A → B.
- Achat B → A.
- Prestation interne.
- Prêt interne.
- Transfert de trésorerie.
- Transfert de stock.
- Frais partagés.
- Autres opérations internes.

```
A ÉMET → INTERCOMPANY → B REÇOIT
A CRÉANCE ↔ B DETTE
```

## 15. Rapprochement intercompany

- Identifier la transaction des deux côtés.
- Comparer montant.
- Comparer devise.
- Comparer date.
- Comparer référence.
- Identifier asymétrie.
- Identifier transaction manquante.
- Proposer rapprochement.
- Validation.
- Historique.

## 16. Éliminations

- Créances/dettes intercompany.
- Produits/charges internes.
- Dividendes internes si applicable.
- Mouvements internes selon périmètre.
- Autres éliminations définies par politique comptable.

Les éliminations sont des ajustements de consolidation et ne modifient pas les livres
individuels.

## 17. Processus de consolidation

```
1. CHOISIR PÉRIODE
2. CHOISIR ENTREPRISES
3. VÉRIFIER CLÔTURES
4. VÉRIFIER MAPPINGS
5. CONVERTIR DEVISES
6. AGRÉGER
7. RAPPROCHER INTERCOMPANY
8. APPLIQUER ÉLIMINATIONS
9. AJUSTEMENTS AUTORISÉS
10. GÉNÉRER ÉTATS CONSOLIDÉS
11. VALIDER / PUBLIER
```

## 18. Statuts de consolidation

- Brouillon.
- En préparation.
- Rapprochement en cours.
- Prêt à valider.
- Validé.
- Publié.
- Réouvert sous procédure contrôlée.

## 19. États consolidés

- Bilan consolidé.
- Compte de résultat consolidé.
- Flux de trésorerie consolidé.
- Balance consolidée.
- Grand livre de consolidation si activé.
- Analyse par entreprise.
- Analyse par secteur.
- Éliminations.
- Ajustements.

## 20. Contrôle de cohérence groupe

- Sommes des entreprises vs consolidation.
- Équilibre bilan.
- Débit = crédit.
- Intercompany rapproché.
- Éliminations documentées.
- Taux de change présents.
- Périodes cohérentes.
- Entreprises incluses confirmées.

## 21. Gestion des accès groupe

- Voir groupe.
- Voir liste entreprises.
- Voir KPI groupe.
- Voir comptabilité multi-entreprises.
- Voir détails d'une entreprise.
- Modifier paramètres groupe.
- Gérer mappings.
- Gérer intercompany.
- Lancer consolidation.
- Valider consolidation.
- Publier consolidation.

Chaque action doit être contrôlée côté serveur et liée au périmètre approprié.

## 22. Sécurité multi-entreprises

- Isolation stricte par company_id/périmètre.
- Contrôles serveur.
- Aucun accès par simple changement d'identifiant dans l'URL.
- Protection des exports.
- Audit des changements de contexte sensibles.
- Audit des accès groupe.
- Session sécurisée.
- Réauthentification pour actions critiques.

## 23. Cabinet comptable / gestion centralisée

NEXORA peut être utilisé par une personne ou une équipe qui supervise plusieurs entreprises,
par exemple un comptable en chef ou un cabinet, sans mélanger leurs écritures.

- Portefeuille d'entreprises.
- Affectation des comptables.
- État des clôtures.
- Liste des anomalies.
- Tâches de contrôle.
- Vue centralisée.
- Accès détaillé seulement aux entreprises autorisées.

## 24. Centre de contrôle du comptable en chef

- Entreprises à clôturer.
- Entreprises clôturées.
- Anomalies comptables.
- Créances importantes.
- Dettes importantes.
- Rapprochements en attente.
- Intercompany non rapproché.
- États prêts à validation.
- Alertes.

## 25. Gestion des tâches groupe

- Tâche.
- Entreprise.
- Responsable.
- Priorité.
- Échéance.
- Statut.
- Commentaire.
- Pièce jointe.
- Historique.

## 26. Politiques groupe

- Devise de consolidation.
- Plan comptable groupe.
- Mapping.
- Périodes.
- Règles intercompany.
- Seuils d'approbation.
- Politiques de dépenses.
- Politiques de crédit.
- Politiques de clôture.
- Règles de reporting.

## 27. Scénario complet

```
GROUPE X
 ├── A : PHARMACIE
 ├── B : RESTAURANT
 └── C : SUPERMARCHÉ

COMPTABLE A → A uniquement
COMPTABLE B → B uniquement
COMPTABLE EN CHEF → A + B + C
DIRECTION → A + B + C + VUE GROUPE

A vend à B
→ INTERCOMPANY
→ A créance / B dette
→ RAPPROCHEMENT
→ ÉLIMINATION
→ CONSOLIDATION
```

## 28. Ajout d'une nouvelle entreprise

- Créer entreprise.
- Choisir activité.
- Choisir devise.
- Configurer taxes.
- Configurer plan comptable.
- Configurer établissements.
- Configurer utilisateurs.
- Configurer accès groupe.
- Configurer intercompany si nécessaire.
- Configurer reporting.
- Commencer les opérations.

L'ajout d'une entreprise ne doit pas nécessiter de nouvelle installation NEXORA.

## 29. Retrait / archivage d'une entreprise

- Désactiver nouvelles opérations.
- Conserver historique.
- Finaliser clôtures.
- Exporter données selon droit.
- Conserver obligations légales.
- Archiver.
- Retirer des vues opérationnelles.
- Conserver données groupe nécessaires à l'historique.

## 30. Multi-activité dans un groupe

- Pharmacie + restaurant + supermarché.
- Chaque entreprise garde son modèle opérationnel.
- Les KPI communs sont normalisés.
- Les KPI sectoriels restent spécialisés.
- La consolidation utilise les comptes mappés, pas les écrans POS.

## 31. Performance

- Chargement uniquement des entreprises autorisées.
- Agrégations groupe optimisées.
- Pagination.
- Cache des KPI lorsque nécessaire.
- Rapports lourds asynchrones.
- Séparer requêtes opérationnelles et analytiques lourdes.

## 32. Audit groupe

- Création/suppression logique d'entreprise.
- Ajout/retrait utilisateur.
- Changement de rôle.
- Changement de scope.
- Modification mapping.
- Opération intercompany.
- Élimination.
- Ajustement consolidation.
- Validation/publication.

## 33. Scénarios de validation

- Créer un groupe avec trois entreprises.
- Créer un comptable pour une seule entreprise.
- Créer un comptable en chef pour trois entreprises.
- Changer de contexte entreprise.
- Vérifier isolation des données.
- Afficher dashboard groupe.
- Créer vente intercompany.
- Créer achat correspondant.
- Rapprocher intercompany.
- Appliquer élimination.
- Convertir devises.
- Générer bilan consolidé.
- Tenter accès interdit.
- Tenter export hors scope.
- Ajouter une quatrième entreprise sans nouvelle installation.
- Archiver une entreprise sans supprimer son historique.

## 34. Critères de sortie

- Groupe défini.
- Entreprise indépendante définie.
- Multi-sites défini.
- Utilisateurs multi-entreprises définis.
- Sélecteur d'entreprise défini.
- Vue groupe définie.
- Comptabilité multi-entreprises définie.
- Mapping groupe défini.
- Intercompany défini.
- Rapprochement défini.
- Éliminations définies.
- Consolidation définie.
- Devise de consolidation définie.
- Sécurité et isolation définies.
- Cabinet/comptable en chef défini.
- Scénarios testables.

## 35. Étape suivante

Passer à la **Phase 10 — Local, Offline, Hybride & Cloud**. Elle définira comment le même
NEXORA fonctionne sur un seul ordinateur/réseau local, sur plusieurs sites connectés, en
cloud ou avec des caisses capables de continuer à fonctionner temporairement hors connexion.

---

**NEXORA — Phase 9 validée → Phase 10 : Local, Offline, Hybride & Cloud**
