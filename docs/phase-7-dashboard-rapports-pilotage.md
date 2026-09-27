# NEXORA — PHASE 7 — DASHBOARD, RAPPORTS & PILOTAGE

**Spécification fonctionnelle du pilotage opérationnel, financier et multi-entreprises**
**Version 1.0**

> Document de référence. Implémentation : module `Reporting` du backend + Command Center React
> (voir [`phase-7-implementation.md`](phase-7-implementation.md)).

---

## 1. Objectif

Transformer les données validées de NEXORA en informations immédiatement utiles à chaque profil : caissier, vendeur, manager, gestionnaire de stock, comptable, comptable en chef, direction et groupe. Le pilotage doit rester fidèle aux données transactionnelles et respecter strictement les périmètres d'accès.

## 2. Principes

- Un indicateur affiché doit avoir une définition claire.
- Les rapports sont dérivés des données sources et ne constituent pas une seconde base de vérité.
- Chaque utilisateur ne voit que les données de son périmètre.
- Les indicateurs financiers distinguent réel, prévisionnel et estimé.
- Les filtres doivent être cohérents entre dashboards et rapports.
- Les chiffres critiques doivent être traçables jusqu'aux transactions sources.
- Le système doit fonctionner pour une seule entreprise ou un groupe.

## 3. Hiérarchie des espaces de pilotage

```
UTILISATEUR
 ├── Mon activité
 ├── Mon établissement
 ├── Mon entreprise
 └── Mon groupe (si autorisé)

CAISSIER → CAISSE / SESSION
MANAGER → ÉTABLISSEMENT / ENTREPRISE
COMPTABLE → ENTREPRISE(S) AUTORISÉE(S)
COMPTABLE EN CHEF → COMPTABILITÉ MULTI-ENTREPRISES
DIRECTION → ENTREPRISE / GROUPE
AUDITEUR → CONSULTATION / CONTRÔLE
```

## 4. Command Center NEXORA

Vue de pilotage principale : santé de l'activité rapidement, sans remplacer les rapports détaillés.

Chiffre d'affaires · marge · bénéfice · dépenses · trésorerie · créances · dettes · stock · ventes du jour · alertes critiques · tendances · comparaisons.

## 5. KPI principaux

CA jour/semaine/mois/année · nombre de transactions · panier moyen · marge brute · taux de marge · bénéfice selon périmètre · dépenses · encaissements · décaissements · solde de trésorerie · créances ouvertes · dettes ouvertes · stock valorisé.

```
PANIER MOYEN = CA / NOMBRE DE VENTES
```

## 6. Filtres universels

Période personnalisée · aujourd'hui/semaine/mois/année · groupe · entreprise · établissement · magasin · caisse · entrepôt · produit · catégorie · client · fournisseur · vendeur/caissier · mode de paiement · statut · devise.

Les filtres disponibles sont déterminés par le périmètre et les permissions de l'utilisateur.

## 7. Dashboard caissier

Ventes de la session · montant encaissé · nombre de ventes · panier moyen · répartition des paiements · retours autorisés · état de caisse · écart éventuel · statut réseau/offline · alertes opérationnelles.

## 8. Dashboard manager

Ventes de l'établissement · performance par caisse · par vendeur · marge · produits les plus vendus · à faible rotation · stock critique · retours · créances clients · dépenses · trésorerie · alertes.

## 9. Dashboard comptable

CA comptabilisé · écritures à contrôler · balance · créances · dettes · caisse · banques · rapprochements · périodes ouvertes/clôturées · anomalies · écritures non équilibrées si brouillons · rapports comptables.

## 10. Dashboard comptable en chef

Vue de plusieurs entreprises autorisées · CA par entreprise · résultat par entreprise · créances groupe · dettes groupe · trésorerie par entreprise · exposition intercompany · état des rapprochements · état des clôtures · indicateurs consolidés · anomalies intercompany.

## 11. Dashboard direction / propriétaire

CA · marge · bénéfice · trésorerie · créances · dettes · stock · dépenses · rentabilité · performance des établissements · tendance · prévisions · alertes critiques · comparaison entre entreprises.

## 12. Dashboard groupe

```
GROUPE
 ├── Entreprise A
 ├── Entreprise B
 ├── Entreprise C
 └── Consolidé
```

CA consolidé · résultat consolidé · trésorerie globale · créances globales · dettes globales · performance comparative · transactions intercompany · éliminations · indicateurs par entreprise · par secteur.

## 13. Rapports de ventes

Journal des ventes · par période · produit · catégorie · client · vendeur · caisse · établissement · mode de paiement · retours et remboursements · remises accordées · taxes collectées.

## 14. Rapports produits

Top produits · faible rotation · sans vente · marge par produit · par catégorie · évolution des prix · plus rentables · plus gros CA.

## 15. Rapports stock

État du stock · valorisation · mouvements · inventaires · écarts · pertes/casse · expiration · lots · transferts · stock minimum · ruptures · rotation · stock dormant.

## 16. Rapports achats

Par fournisseur · par période · par produit · commandes ouvertes · réceptions · réceptions partielles · factures fournisseurs · achats à crédit · prix fournisseurs · évolution des coûts · retours fournisseurs.

## 17. Rapports créances

Créances ouvertes · par client · par échéance · échues · aging · paiements reçus · partiels · limites de crédit · clients à risque · historique des relances.

## 18. Aging clients

```
NON ÉCHU | 1–30 J | 31–60 J | 61–90 J | >90 J  →  PRIORISATION DES RELANCES
```

Les tranches sont configurables selon les besoins de l'entreprise.

## 19. Rapports dettes

Ouvertes · par fournisseur · échéances · échues · aging fournisseurs · paiements · partiels · prévision des décaissements · fournisseurs prioritaires.

## 20. Rapports caisse

Sessions · ventes · encaissements · décaissements · fond initial · solde théorique · solde compté · écarts · retraits/dépôts · transferts · rapport par caissier · par terminal.

## 21. Rapports finance

Trésorerie · flux de trésorerie · encaissements · décaissements · dépenses · revenus · banques · Mobile Money · prévisions · budget vs réalisé · rapprochements.

## 22. Rapports comptables

Journal général · grand livre · balance · bilan · compte de résultat · flux de trésorerie · balance auxiliaire clients · balance auxiliaire fournisseurs · journaux par type · écritures par source · mouvements par compte · périodes.

## 23. Rapports analytiques

Rentabilité par entreprise · établissement · activité · catégorie · centres de coûts · budgets · écarts · contribution au résultat · comparaisons temporelles.

## 24. Budget vs réalisé

```
ÉCART = RÉALISÉ − BUDGET
ÉCART % = (RÉALISÉ − BUDGET) / BUDGET × 100
```

Budget par compte · centre de coûts · entreprise · période · révisions autorisées · commentaires d'écart.

## 25. Prévisions

Ventes · trésorerie · encaissements · décaissements · créances · dettes · scénarios prudent/normal/optimiste · distinction claire entre prévision et réel.

## 26. Analyse de performance

Comparaison période actuelle vs précédente · variation montant · variation % · tendance · meilleurs établissements · produits · vendeurs · entreprises en croissance · sous objectif.

## 27. Objectifs et alertes KPI

Objectif CA · marge · dépenses · trésorerie · stock · recouvrement · seuils configurables · alertes lorsque seuil dépassé ou non atteint.

## 28. Centre d'alertes

Stock faible · expiration · créance échue · dette échue · écart caisse · dépense inhabituelle · baisse forte des ventes · variation anormale de marge · trésorerie faible · erreur de synchronisation · anomalie comptable · anomalie intercompany.

## 29. Drill-down

Tout KPI important doit permettre, selon les permissions, de descendre progressivement vers sa source.

```
KPI CA   → ENTREPRISE → JOUR → CAISSE → VENTE → LIGNE → PRODUIT
CRÉANCE  → CLIENT → FACTURE → PAIEMENTS → TRANSACTIONS
CHARGE   → COMPTE → ÉCRITURE → SOURCE MÉTIER
```

## 30. Exports

PDF · Excel/XLSX · CSV · impression · export comptable selon format prévu · exports filtrés · conformes aux permissions · historique des exports sensibles.

## 31. Rapports planifiés

Quotidien · hebdomadaire · mensuel · clôture · trésorerie · créances/dettes · stock · destinataires configurables · périmètre enregistré · journal d'exécution.

## 32. Notifications

Notification dans NEXORA · email selon config · push si activé · résumé quotidien · alerte immédiate pour événements critiques · regroupement pour éviter le spam · respect du périmètre.

## 33. Ask Nexora

Couche d'analyse en langage naturel sur les données autorisées :

- « Combien avons-nous vendu ce mois-ci ? »
- « Quelle entreprise a la meilleure marge ? »
- « Quels clients ont des créances échues ? »
- « Pourquoi la trésorerie baisse-t-elle ? »
- « Quels produits sont proches de la rupture ? »

Les réponses citent ou permettent d'ouvrir les données sources, distinguent faits et prévisions, respectent les permissions.

## 34. Qualité des indicateurs

Définition documentée de chaque KPI · source de données identifiée · date de dernière actualisation · statut réel/prévisionnel/estimé · gestion des arrondis · des devises · réconciliation avec comptabilité · aucune modification manuelle silencieuse.

## 35. Performance des rapports

Pagination des listes · filtres indexés · agrégations optimisées · cache pour indicateurs coûteux · exports lourds asynchrones · agrégats préparés pour gros volumes · ne pas ralentir le POS.

## 36. Sécurité des rapports

Contrôle de périmètre serveur · masquage des données sensibles · permissions d'export · audit des exports financiers · protection des liens de rapports · expiration des téléchargements temporaires.

## 37. Scénarios de validation

Caissier voit uniquement sa session · manager son établissement · comptable ses entreprises · comptable en chef plusieurs entreprises autorisées · direction le groupe si autorisée · KPI CA réconcilié avec ventes · créances réconciliées avec sous-livre · balance équilibrée · dashboard groupe exclut une entreprise non autorisée · export respecte filtres et permissions · drill-down retrouve les transactions sources · rapport planifié s'exécute avec le bon périmètre · une donnée prévisionnelle n'est pas présentée comme réelle.

## 38. Critères de sortie

- Dashboards par rôle définis.
- Command Center défini.
- KPI définis et documentés.
- Filtres et périmètres définis.
- Rapports ventes/stock/achats définis.
- Créances/dettes définies.
- Finance et comptabilité définies.
- Consolidation définie.
- Drill-down défini.
- Exports définis.
- Rapports planifiés définis.
- Alertes définies.
- Ask Nexora défini avec garde-fous.
- Performance et sécurité définies.

## 39. Étape suivante

Passer à la **Phase 8 — Modules sectoriels**. Elle détaillera comment NEXORA s'adapte à la pharmacie, au restaurant, au supermarché, au grossiste, aux services et à d'autres activités tout en conservant le même noyau.

---

**NEXORA — Phase 7 validée → Phase 8 : Modules sectoriels**
