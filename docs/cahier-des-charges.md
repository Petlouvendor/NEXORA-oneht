# CAHIER DES CHARGES FONCTIONNEL ET TECHNIQUE — NEXORA

**Nexora — Plateforme universelle de gestion commerciale, POS, financière et comptable multi-entreprises**
**Version 1.0**

> Document de référence. Le projet est construit phase par phase, dictées par le porteur du projet.

---

## 1. Objet et vision

Développer une plateforme universelle capable de s'adapter aux micro-entreprises, PME, grandes entreprises et groupes, à plusieurs secteurs et à plusieurs modes de fonctionnement. Toutes les fonctionnalités prévues appartiennent à la V1.0 ; elles peuvent être configurées selon le contexte.

## 2. Objectifs fonctionnels

- Gestion multi-entreprises et multi-établissements
- POS et ventes
- Achats et fournisseurs
- Stock et inventaires
- Clients et ventes à crédit
- Achats à crédit et dettes fournisseurs
- Caisse, banque et trésorerie
- Finance et budgets
- Comptabilité individuelle et consolidée
- Gestion des utilisateurs et périmètres
- Offline / synchronisation
- Rapports, alertes, analyse et IA
- Adaptation aux secteurs et au marché haïtien

## 3. Structure organisationnelle

```
PLATFORM → USER → GROUP → COMPANY → ESTABLISHMENT → STORE/WAREHOUSE → POS
```

Un groupe peut contenir plusieurs entreprises. Une entreprise peut avoir plusieurs activités, établissements, magasins, entrepôts et caisses.

## 4. Activités et configuration

- Supermarché
- Épicerie
- Pharmacie
- Restaurant
- Boutique
- Hôtel
- Quincaillerie
- Grossiste
- Distributeur
- Services
- Production
- E-commerce
- Autre

Le type d'activité ne doit pas créer une version distincte. Les différences reposent sur les modules, la configuration et les règles métier.

## 5. Niveaux de complexité

| Niveau | Périmètre |
|---|---|
| **SIMPLE** | POS + Stock + Caisse + Clients + Fournisseurs + Dépenses |
| **STANDARD** | + Achats + Banque + Finance + Comptabilité + CRM |
| **AVANCÉ** | + Multi-sites + Budgets + Workflows + Consolidation + Audit |
| **ENTERPRISE** | + Multi-entreprises + SSO + API + BI + intégrations |

Une entreprise doit pouvoir évoluer d'un niveau à l'autre sans changer de produit.

## 6. Modes de déploiement

- **Local** : application et données utilisables sur un ordinateur ou serveur local.
- **Hybride** : sites locaux synchronisés avec un environnement central.
- **Cloud** : fonctionnement centralisé pour grandes entreprises et groupes.
- **Migration progressive** Local → Hybride → Cloud sans perte de données.

## 7. Offline et synchronisation

- Le POS doit continuer les opérations essentielles hors ligne.
- Chaque opération possède un identifiant unique.
- Synchronisation automatique au retour de la connexion.
- Prévention des doublons.
- Détection des conflits.
- Optimisation pour les connexions faibles.

```
PENDING → SYNCING → SYNCED
                 ↘ FAILED / CONFLICT
```

## 8. Utilisateurs, rôles et périmètres

Modèle : **Utilisateur + Rôle + Permission + Périmètre**.

- Super administrateur
- Administrateur groupe
- Propriétaire / Direction
- Manager
- Comptable
- Comptable senior
- Comptable en chef
- Caissier
- Gestionnaire de stock
- Responsable achats
- Vendeur
- Auditeur

Un périmètre peut être défini au niveau groupe, entreprise, établissement, magasin, entrepôt ou caisse.

## 9. Comptabilité

- Partie double
- Plan comptable
- Journaux
- Écritures
- Grand livre
- Balance
- Bilan
- Compte de résultat
- Flux de trésorerie
- Périodes et clôtures
- Comptabilité indépendante par entreprise

## 10. Comptabilité consolidée et inter-entreprises

Le comptable en chef ou la direction autorisée pourra consulter plusieurs entreprises et produire une vision consolidée. Les opérations inter-entreprises devront être identifiables et éliminables dans les états consolidés.

```
Entreprise A + B + C → opérations internes → éliminations → états consolidés
```

## 11. POS et ventes

- Scan et recherche
- Panier et quantités
- Remises et taxes
- Clients
- Ventes au comptant
- Ventes à crédit
- Paiements multiples
- Retours et remboursements
- Reçus
- Clôture de caisse

## 12. Ventes à crédit

- Créance client
- Limite de crédit
- Paiement total ou partiel
- Échéance
- Retards
- Historique des règlements
- Relevé
- Relances
- Intégration aux prévisions de trésorerie et à la comptabilité

## 13. Produits et stocks

- Produits, catégories, marques et unités
- SKU et codes-barres
- Prix d'achat et de vente
- Prix par quantité ou client
- Stocks minimum/maximum
- Lots et expiration
- FEFO
- Inventaires
- Transferts
- Ajustements
- Produits au poids
- Production et recettes

## 14. Achats

- Fournisseurs
- Commandes
- Réceptions
- Factures fournisseurs
- Achats au comptant
- Achats à crédit
- Paiements
- Retours
- Historique des prix
- Dettes fournisseurs

```
Commande → Réception → Stock → Facture → Dette/Paiement → Comptabilité
```

## 15. Achats à crédit

Le système doit gérer les achats dont le règlement intervient après réception ou facturation.

- Montant total
- Montant payé immédiatement
- Solde restant
- Paiements partiels
- Date d'échéance
- Conditions 30/60/90 jours ou personnalisées
- Limite de crédit fournisseur
- Remise pour paiement anticipé si applicable
- Pénalités si applicables
- Retards
- Alertes d'échéance
- Historique des règlements
- Relevé fournisseur
- Intégration aux dettes et à la prévision de trésorerie

## 16. Créances et dettes

```
VENTE À CRÉDIT : Entreprise → Client → Créance → Encaissement
ACHAT À CRÉDIT  : Fournisseur → Entreprise → Dette → Décaissement
```

Les créances clients et dettes fournisseurs doivent être suivies séparément et ensemble dans la vision de trésorerie.

## 17. Finance et trésorerie

- Revenus
- Dépenses
- Caisse
- Banques
- Mobile Money
- Transferts
- Créances
- Dettes
- Budgets
- Prévisions
- Rapprochement bancaire

## 18. Paiements et devises

- Cash HTG
- Cash USD
- Carte
- Virement
- Chèque
- Mobile Money et fournisseurs intégrables
- Crédit
- Paiements multiples
- HTG, USD, EUR, CAD et autres devises
- Taux de change historique par transaction

## 19. Modules spécialisés

- **Pharmacie** : médicaments, lots, expiration, FEFO, prescriptions, ordonnances
- **Restaurant** : tables, commandes, cuisine, KDS, recettes, ingrédients, production, pourboires
- **Retail** : scanner, promotions, poids, étiquettes, inventaires, entrepôts
- **Grossiste** : tarifs quantité, commandes, crédit, représentants, livraison

## 20. CRM, commandes et livraison

- Fiches clients et historique
- Fidélité
- Promotions et coupons
- Commandes à distance
- Intégration possible WhatsApp
- Livreurs et zones
- Preuve de livraison
- Paiement à la livraison

## 21. Rapports et tableaux de bord

- Ventes par période, entreprise, établissement, caisse, vendeur, produit et catégorie
- Stock et rotation
- Créances et dettes
- Revenus et dépenses
- Trésorerie
- Rapports comptables
- Comparaison entre entreprises
- Vue consolidée du groupe

## 22. Business Health et IA

- Score de santé de l'entreprise
- Prévision des ventes
- Prévision de trésorerie
- Recommandations de réapprovisionnement
- Analyse de rentabilité
- Détection d'anomalies et comportements inhabituels

## 23. Approbations et audit

- Seuils d'approbation configurables pour dépenses, remises, remboursements, achats, crédits et ajustements
- Journal des actions sensibles
- Utilisateur, date, entreprise, terminal et modification enregistrés
- Protection des périodes clôturées

## 24. Sécurité

- Authentification sécurisée
- MFA
- Rôles et permissions
- Périmètres d'accès
- Protection des sessions
- Chiffrement des données sensibles
- Sauvegardes et restauration
- Contrôle des appareils
- Alertes d'activité inhabituelle

## 25. Documents, import/export et intégrations

- Factures, devis, pro forma, bons de commande, bons de livraison, avoirs et relevés
- Import produits/clients/fournisseurs/stocks/prix
- Export CSV/Excel
- Rapports PDF
- API pour e-commerce, mobile, paiements, livraison et partenaires
- Intégration scanners, imprimantes, tiroirs-caisses, balances et afficheurs

## 26. Architecture fonctionnelle

```
CORE
├── Groupes / entreprises / utilisateurs
├── POS / Commerce
├── Stock
├── Achats
├── Clients / Fournisseurs
├── Finance
├── Comptabilité
├── CRM
├── Modules spécialisés
├── Reporting / BI
└── IA / analyse
```

## 27. Architecture technique cible

**Backend** : C# / ASP.NET Core.
**Frontend** : React / TypeScript.
**Base de données** : PostgreSQL.

L'organisation devra rester modulaire et évolutive, avec un noyau commun et des modules métier configurables.

## 28. Règles financières

- Utiliser une précision décimale adaptée aux montants financiers.
- Garantir l'atomicité des opérations critiques.
- Ne pas supprimer silencieusement une opération validée.
- Conserver les corrections et l'historique.
- Respecter la partie double.
- Débit total = crédit total.
- Protéger les périodes clôturées.
- Conserver le taux de change utilisé.
- Séparer créances clients et dettes fournisseurs.
- Identifier les transactions inter-entreprises.

## 29. Critères de réussite

- Petite entreprise utilisable en local.
- Entreprise en croissance utilisable en hybride.
- Grande entreprise utilisable en ligne/centralisé.
- Évolution sans changement de produit.
- Gestion multi-entreprises.
- Accès par rôle et périmètre.
- Comptabilité individuelle et consolidée.
- Ventes et achats à crédit avec paiements partiels.
- Créances et dettes intégrées à la trésorerie.
- Vente intégrée au stock, finance et comptabilité.
- Achat intégré au stock, dette, paiement et comptabilité.
- Synchronisation sans doublons.
- Rapports individuels et consolidés cohérents.
- Traçabilité des opérations sensibles.

## 30. Principe directeur

> L'entreprise doit pouvoir s'adapter au logiciel, mais le logiciel doit surtout être capable de s'adapter à l'entreprise.
