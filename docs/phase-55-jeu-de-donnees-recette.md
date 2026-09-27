# NEXORA ONE — Jeu de données de recette (Phase 55)

Jeu de données **dédié, reproductible et conservé** pour la recette manuelle, les démonstrations et
les régressions (grille §5, anomalie BUG-015). Il couvre les scénarios de la grille Phase 55 sur
~100 jours d'activité simulée, pour deux entreprises opérationnelles.

## 1. Principes

- **Base séparée** `nexora_recette`. La base `nexora` n'est jamais touchée : elle sert de modèle
  (TEMPLATE) aux tests d'intégration (anomalie A3, Phase 49). Le script et le générateur refusent
  tous deux de la cibler.
- **Tout passe par les services métier réels** (ventes, achats, paiements, stock, comptabilité,
  crédit, utilisateurs) exécutés en processus, exactement comme l'API. Les données sont donc
  cohérentes entre modules : stock ↔ mouvements, ventes ↔ écritures, créances ↔ règlements. Un refus
  d'un service arrête la génération : aucune donnée incohérente silencieuse.
- **Seul le référentiel sans API est posé en SQL** : l'établissement, les emplacements, comptes,
  correspondances comptables et séries documentaires du Supermarché
  (`database/seeds/recette/01_supermarche_config.sql`), les promotions (pas d'API de création), et
  deux ajustements de date documentés : la validité des prix des 2 produits de la démo (qui datent
  du chargement) et la `order_date` des commandes fournisseur (le service ne l'antidate pas).
- **Déterministe** : graine aléatoire fixe (55), dates relatives au jour de génération (J = aujourd'hui).

## 2. Construire / reconstruire

```bash
PGPASSWORD=<mot de passe postgres> ./database/scripts/recette.sh            # base nexora_recette
psql -d nexora_recette -f database/seeds/recette/verif_jeu_recette.sql      # contrôle du jeu
```

Étapes : schéma complet (V001–V063) → démo ABC GROUP → Supermarché opérationnel → générateur
`backend/tools/Nexora.RecetteSeed` (6 à 30 min selon la charge du poste). Relancer le script reconstruit la base de zéro.

Pointer l'application sur la base de recette :

```bash
Database__ConnectionString="Host=localhost;Database=nexora_recette;Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public" \
  dotnet run --project backend/src/Nexora.Api
```

## 3. Comptes

Comptes démo (inchangés) : `direction@abc-group.demo` / `Nexora!2026` (toutes entreprises), etc.
Comptes de recette — mot de passe commun **`Recette2026!`** :

| Compte | Entreprise | Rôle | Usage recette |
|---|---|---|---|
| caisse1.super@recette.demo, caisse2.super@recette.demo | Supermarché | cashier | POS, plafond de remise, pas de changement de prix |
| vendeur.super@recette.demo | Supermarché | salesperson | devis, commandes |
| stock.super@recette.demo | Supermarché | stock_manager | inventaires, pertes, lots |
| achats.super@recette.demo | Supermarché | purchasing_manager | commandes, réceptions, BUG-013 |
| compta.super@recette.demo | Supermarché | accountant | finance, clôtures |
| auditeur.super@recette.demo | Supermarché | auditor | lecture seule (RBAC) |
| gerant.super@recette.demo | Supermarché | manager | pilotage, Intelligence |
| ancien.employe@recette.demo | Supermarché | cashier — **suspendu** | AUTH : connexion refusée |
| caisse.pharma@, stock.pharma@, compta.pharma@, gerant.pharma@recette.demo | Pharmacie | cashier / stock_manager / accountant / manager | isolation multi-entreprise |

## 4. Contenu par entreprise

| | Pharmacie ABC | Supermarché ABC |
|---|---|---|
| Produits | 16 (14 suivis par lot) | 32 (4 vendus au poids) |
| Clients / fournisseurs | 6 / 3 | 8 / 5 |
| Ventes validées (≈ 100 jours, 4 mois civils) | ≈ 440 | ≈ 2 030 |
| Réceptions / factures fournisseur | 6 / 6 | 17 / 17 |
| Écritures comptables | ≈ 485 | ≈ 2 090 |
| Ventes en USD | 2 | — |

Valeurs indicatives : les dates étant relatives au jour de génération, les volumes varient
légèrement d'une génération à l'autre. Durée : 6 min sur un poste au repos, jusqu'à 30 min sous charge.

### Pharmacie ABC (tendance récente en **baisse** : −30 % puis −55 % sur les 14 derniers jours)

| Donnée | Scénario couvert |
|---|---|
| 16 produits dont 14 suivis par lot (PARA500, AMOX1G de la démo + 14 `PH-*`) | PROD, lots, FEFO |
| `PH-ART` : lot expirant à J+20 | alerte péremption (STOCK) |
| `PH-SIR` : lot **expiré** (J−5) toujours en stock, jamais vendu | balayage / blocage des lots expirés |
| `PH-AUG`, `PH-AZI`, `AMOX1G` : médicaments sur ordonnance | secteur pharmacie |
| `PH-SOL` dormant, `PH-CREM` surstock | recommandation « produit dormant / surstock » |
| `PH-BAIN` stock bas (délai 14 j) | recommandation « réapprovisionnement » |
| `PH-VITC` : prix USD 2,10 ; 2 ventes en USD encaissées sur le compte bancaire USD | multi-devises (FIN-010) |
| `PH-BROS` : prix plancher 100 | refus sous le plancher |
| Fournisseur « Laboratoire du Sud (nouveau) » sans aucun historique | §29 fournisseur vide |
| Clients : Clinique Bon Samaritain (bon payeur, règlements partiels), Dispensaire de Carrefour (3 crédits jamais réglés → échus), Centre Médical Delmas 60 (proche de la limite de 40 000), M. Robert Étienne (inactif depuis J−70), Mme Claudette Pierre (comptant), Mlle Nadège Jean (aucun historique) | CLI, relances, recouvrement |
| Session de caisse du jour **laissée ouverte** | contrôle « session ouverte » à la clôture financière |

### Supermarché ABC (tendance récente en **hausse** : +20 % puis +60 % sur les 14 derniers jours)

| Donnée | Scénario couvert |
|---|---|
| 32 produits `SM-*`, 7 rayons, TVA 10 % sur une partie | §5 « 30 produits variés », marges et rotations variées |
| `SM-BAN`, `SM-TOM`, `SM-OIG`, `SM-AVO` : vendus **au kg** (3 décimales), codes PLU | produits pesés |
| `SM-EAU15` : palier de prix dès 12 unités (65 au lieu de 75) | prix par quantité |
| `SM-LAI400`, `SM-RHU`, `SM-LAIB` : prix planchers | refus sous le plancher |
| `SM-HUI1` : coût fournisseur **+25 %** à partir de J−30 (5 commandes) | recommandation « hausse de prix fournisseur » |
| `SM-HAR` : stock bas puis rupture (délai 12 j) | alerte + réapprovisionnement |
| `SM-KET` dormant, `SM-VIN` surstock | dormant / surstock |
| `SM-PAIN` : **aucun fournisseur** référencé | §29 produit sans fournisseur |
| Promotion « Semaine des boissons −10 % » active (J−12 → J+9) ; « Opération riz » expirée | moteur de promotions |
| Ticket de **25 lignes** (J−3) | §29 très grand panier |
| École Saint-Joseph : achat à crédit de ~55 000 HT avec acompte de 10 000, puis règlement de 40 % du solde | §29 achat très élevé, règlement partiel |
| Restaurant Le Bon Goût (bon payeur), Épicerie Ti Marché (crédits échus), Hôtel Montagne (proche de la limite), Boutique Fermée SARL (**bloquée**), Jean-Pierre Louis (inactif), Marie Joseph (comptant), client sans historique | CLI-004 à CLI-008 |
| Session de caisse du jour **clôturée avec un écart de −150** | POS-017/018 écart de caisse |
| 9 comptes utilisateurs dont 1 suspendu | RBAC, AUTH |

### Commun aux deux entreprises

| Donnée | Scénario couvert |
|---|---|
| Achats J−100, J−55, J−15 : commande → approbation → envoi → réception datée → facture → rapprochement 3-way → paiement | ACH-001 à ACH-008 |
| Dernier fournisseur de J−100 **jamais payé** (dette échue) | dettes fournisseurs échues, balance âgée |
| Réassort J−15 : **réception partielle** (1er fournisseur, 60 %) | ACH-004 |
| Réassort J−15 : facture du 2ᵉ fournisseur **+8 %** sur le prix → rapprochement en écart | ACH-006 / ACH-008 paiement bloqué |
| ~100 jours de ventes datées (espèces, carte, mobile money, paiement mixte, ~5 % remisées) | POS, rapports par période, prévisions |
| Dépenses : loyer mensuel, électricité hebdomadaire, **dépense inhabituelle à J−3** | FIN, anomalie de dépenses (voir BUG-016) |
| Casse enregistrée à J−40 | pertes de stock |
| Aujourd'hui : retour **partiel** et retour **total**, panier en attente **annulé** et panier en attente **conservé**, inventaire avec écarts (−2 / +1), devis envoyé, commande client confirmée et réservée, objectif de CA mensuel | POS-012/014/015/016, STOCK-007/008, ventes B2B, pilotage |
| Moteurs exécutés en fin de génération : recommandations, anomalies, prévisions CA 7/30 j et trésorerie 30 j, scores clients/fournisseurs/produits | INT, ANL |

## 5. Signaux attendus (contrôlés après génération)

Contrôlés par `verif_jeu_recette.sql` sur la génération du 2026-09-22 :

| Domaine | Signal attendu | Obtenu |
|---|---|---|
| Crédit | Centre Médical Delmas 60 à ~83 % de sa limite (33 020 / 40 000), Hôtel Montagne à ~90 % (54 185 / 60 000) | ✅ |
| Crédit | Créances échues : Dispensaire de Carrefour, Épicerie Ti Marché, Boutique Fermée (bloquée) | ✅ |
| Crédit | Bons payeurs sans échu : Restaurant Le Bon Goût ; École Saint-Joseph (≈ 27 500 restant après acompte et règlement partiel) | ✅ |
| Fournisseurs | Dette échue (PharmaPlus Import, Hygiène Pro Distribution) ; 1 facture en écart de rapprochement par entreprise | ✅ |
| Stock | Articles sous le point de commande, dont ruptures (SM-HAR, SM-PAIN…) | ✅ |
| Stock | Dormants : PH-SOL, PH-SIR, SM-KET | ✅ |
| Lots | PH-SIR lot **expiré** encore en stock ; PH-ART lot expirant sous 30 jours | ✅ |
| Caisse | Supermarché : session clôturée, écart −150 ; Pharmacie : session ouverte | ✅ |
| POS | 1 retour partiel, 1 retour total, 1 panier en attente, 1 vente en attente annulée (`void`) par entreprise | ✅ |
| Promotions | 1 active (boissons), 1 expirée ; ≈ 200 applications en caisse | ✅ |
| Recommandations | réassort, dormant/surstock, priorisation du recouvrement, client inactif (les deux entreprises) ; **baisse des ventes** (pharmacie) ; **hausse des ventes** et **hausse de prix fournisseur** (supermarché) | ✅ |
| Anomalies | Pharmacie : « CA en baisse sur 7 jours » | ✅ |
| Anomalies | Dépenses en hausse | ❌ impossible — BUG-016 (registre `expense` jamais alimenté) |
| Anomalies | Marge en baisse, stock négatif, écart de trésorerie projeté | non provoqués (voir §7) |
| Prévisions / scores | CA 7 et 30 j, trésorerie 30 j ; scores clients, fournisseurs, produits | ✅ |
| Comptes | 13 comptes de recette dont 1 suspendu | ✅ |

## 6. Anomalies révélées par la génération du jeu

Construire un jeu réaliste en passant par les services réels a fait apparaître **cinq défauts que
les suites de tests ne voyaient pas** (chaque test ne crée que des cas mono-ligne) :

| Réf. | Défaut | Gravité | Traitement |
|---|---|---|---|
| BUG-016 | `POST /finance/expenses` ne crée que l'écriture comptable : **aucune ligne dans `nexora.expense`** (aucun code n'y écrit). Les rapports de dépenses et l'anomalie « dépenses en hausse » ne peuvent jamais se déclencher | Moyenne | **Ouvert** — le jeu passe par ce même service pour que la recette le révèle |
| BUG-017 | Poser un prix dans une autre devise **fermait le prix existant** de la devise principale (la clôture du prix précédent ignorait la devise) | Haute | Corrigé — `CatalogService.SetPriceAsync` |
| BUG-018 | **Toute réception fournisseur de plus d'une ligne échouait** (même UUID d'opération sur chaque mouvement de stock, contrainte d'unicité) ; même défaut sur les retours clients multi-lignes | Critique | Corrigé — seul le premier mouvement porte l'UUID (ancre d'idempotence) |
| BUG-019 | Un article **sans prix en vigueur** passait en caisse à 0 (seule une contrainte comptable finissait par bloquer, avec une erreur technique) | Haute | Corrigé — refus `sale.no_price` dans `ISalePricingGuard` |
| BUG-020 | Une réception rattachée à une commande mais sans ligne de commande **rétrogradait la commande** de « envoyée » à « approuvée » | Basse | Corrigé — le statut n'est plus jamais rétrogradé |

Preuves : `backend/tests/Nexora.IntegrationTests/RecetteDatasetFindingsTests.cs` (4 tests).

Constat complémentaire (pas un défaut) : l'annulation (`void`) d'une vente **validée** est refusée
par conception (§24 : correction par retour total) ; seule une vente **en attente** s'annule. Le jeu
contient les deux cas, ce qui rend BUG-014 testable manuellement.

## 7. Limites connues du jeu

- La Restauration (3ᵉ entreprise de la démo) n'est pas alimentée : elle reste l'entreprise « vide »
  utile aux tests d'isolation et d'écrans sans données.
- Les paiements fournisseurs, retours et inventaires sont datés du jour de génération (les services
  ne les antidatent pas) ; les ventes, réceptions, factures, règlements clients et dépenses sont datés.
- Aucune donnée hors ligne (file de synchronisation) : ces scénarios se jouent depuis le POS.
- Marge en baisse, stock négatif et écart de trésorerie projeté ne sont pas provoqués (voir §5).
