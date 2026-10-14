# 00 — Résumé exécutif : audit technique de NEXORA ONE

> Audit du 13 au 14 octobre 2026, branche `develop`, HEAD `e5e243d`, sur le poste de développement. Méthode : lecture
> du code ligne à ligne (183 fichiers), contrôles automatiques sur le reste, exécution des suites de tests, scénarios
> exécutés contre l'API de HEAD sur une base clonée, requêtes SELECT sur les bases existantes. **Aucune correction,
> aucune migration, aucune écriture dans une base existante de la part de l'audit** (voir l'incident T-002 en §H).
> Détails : fichiers 01 à 11 de ce dossier.

## A. État général

NEXORA ONE est une application de gestion d'entreprise étendue (caisse, stock, achats, ventes, clients, trésorerie,
comptabilité en partie double, paie, rapports, GED, groupe, plateforme SaaS) dont l'essentiel est **réellement
implémenté et relié** : sur 637 appels directs du frontend, 636 aboutissent à une route existante ; aucune donnée
factice n'a été trouvée dans les écrans ; les fournisseurs simulés sont déclarés comme tels et refusés en production.

- **Technologies** : .NET 10 (ASP.NET Core, EF Core 10, Npgsql), PostgreSQL 18 (RLS), React 19, Vite 6, TypeScript 5.7,
  Vitest 4, Playwright, Caddy, Docker Compose, GitHub Actions, MailKit, Twilio, Firebase Admin, ClosedXML, PDFsharp.
- **Maturité observée** : élevée sur l'ossature (transaction unique par opération, moteur comptable équilibré sur
  2 580 écritures de recette, stock cohérent sur 5 836 mouvements, sécurité des sessions, 774 cas de test verts
  côté backend et 275 côté frontend) ; **insuffisante sur des chemins métier précis** qui faussent l'argent et le stock.
- **Vérifié à l'exécution** : connexion, confirmation forte liée à la session, refus d'une entreprise étrangère, vente
  nominale, clôture de caisse, transferts, protection SSRF (tests unitaires), service des comptes clients (tests
  d'intégration), cohérence des données existantes.
- **Partiel** : caisse (retours, apports/sorties), inventaire, multidevise, clôture d'exercice, rôles (suspension),
  rapports (semaine, âgé), plateforme SaaS (écrans éditeur).
- **Simulé** : fournisseur de paiement « simulation » (déclaré, refusé en production). Assistant d'intelligence à
  règles déterministes (pas d'IA générative, ce que le code ne prétend pas).
- **Non vérifiable** : MonCash, SMTP, Twilio, FCM (pas d'identifiants) ; suite E2E (Docker arrêté) ; Caddy.
- **Risques majeurs** : 4 anomalies CRITIQUES (§E), dont une fuite et une modification de données entre entreprises par
  la console d'exploitation dans un déploiement SaaS multi-clients (A-031).
- **Limites de l'audit** : 503 fichiers de code non relus ligne à ligne (couverts seulement par compilation, tests et
  recherche de motifs) ; scénarios d'écriture arrêtés après le refus du contrôle de permissions de l'outil ; pas de
  test de charge, pas de test d'intrusion, pas de navigateur.

**Pas de pourcentage global** : un score unique masquerait la nature des défauts. Les comptes ci-dessous sont des
dénombrements, pas des notes.

| Indicateur | Valeur |
|---|---|
| Anomalies | 4 CRITIQUES, 6 ÉLEVÉES, 13 MOYENNES (dont T-002), 10 FAIBLES, 2 observations (T-001, P-001) |
| Fonctionnalités de la matrice (04) | 46 lignes : 9 FONCTIONNEL VÉRIFIÉ, 7 DÉFECTUEUX, 6 PARTIELLEMENT IMPLÉMENTÉ, 1 PRÉSENT MAIS NON CONNECTÉ, 1 SIMULÉ OU STATIQUE, 2 NON VÉRIFIABLE, 2 ABSENT, 18 IMPLÉMENTÉ, MAIS NON VÉRIFIÉ |

## B. Architecture réelle

Monolithe modulaire .NET en couches (Api, Application, Contracts, Domain, Infrastructure) sur PostgreSQL, SPA React.
La majorité des règles métier vit dans 57 000 lignes de services SQL de l'infrastructure, et non dans le domaine
(1 571 lignes). Isolation par RLS forcée sur 250 tables. Détail et écarts entre l'architecture visée et l'architecture
présente : `01-architecture-reelle.md`.

## C. Inventaire

1 113 fichiers suivis par Git. **963 pertinents** (hors 137 documents et 13 binaires ou générés). Lus ligne à ligne :
**183**. Exécutés via les suites de tests : 174 fichiers de test. Contrôlés par le catalogue : 103 migrations.
**Restant à lire ligne à ligne : 503 fichiers de code**, dont 69 contrôleurs, 25 composants frontend partagés,
15 services de rapports. Registre : `02-inventaire-fichiers.csv` et `03-audit-fichier-par-fichier.md`.

## D. État des modules

Voir `04-matrice-fonctionnalites.md` (matrice de vérité et section D par module).

## E. Anomalies (détail : `09-anomalies-et-priorites.md`)

| ID | Gravité | Résumé | Preuve |
|---|---|---|---|
| A-031 | CRITIQUE (SaaS) | Console `/ops/*` de portée plateforme ouverte aux rôles d'entreprise : lecture des données d'autres entreprises, modification de leurs clients, fournisseurs, produits et utilisateurs, gel des écritures de toute la plateforme | Lecture + catalogue |
| A-002 | CRITIQUE | Remboursement espèces sans mouvement de session : faux manquant, trésorerie diminuée deux fois, charge fictive | Exécuté |
| A-018 | CRITIQUE | Solde des comptes financiers perdu sous concurrence (525 HTG sur 20 opérations) | Exécuté |
| A-021 | CRITIQUE | Bilan doublé après génération des à-nouveaux | Lecture |
| A-001 | ÉLEVÉ | La solution ne compile pas dans la copie de travail (modification non commitée) | Exécuté |
| A-003 | ÉLEVÉ | Vente antidatée et vente ajoutée à une caisse clôturée | Exécuté |
| A-007 | ÉLEVÉ | Inventaire faux si des ventes ont lieu pendant le comptage ; lots ignorés | Exécuté (partiel) |
| A-012 | ÉLEVÉ | Coupon fixe multiplié par le nombre de lignes | Exécuté |
| A-017 | ÉLEVÉ | Aucune saisie possible des taux de change | Lecture + données |
| A-020 | ÉLEVÉ | Clôture d'exercice fausse dès le 2e exercice | Lecture |

Plus 13 MOYENNES (A-004, A-005, A-006, A-008, A-009, A-010, A-013, A-014, A-016, A-019, A-022, A-023, T-002) et
10 FAIBLES (A-011, A-015, A-024 à A-030, A-032). A-031 est sans effet dans une installation mono-entreprise ou mono-groupe.

## F. API

1 029 points d'accès dans 74 contrôleurs ; 18 anonymes, tous justifiés sauf `/metrics` (A-024) ; 954 protégés par une
permission explicite ; 41 à confirmation forte ; 53 avec clé d'idempotence. Inventaire complet et problèmes :
`05-audit-api.md`.

## G. Base de données

291 tables, 890 clés étrangères, 641 CHECK, 476 déclencheurs, 99 migrations appliquées ; rôle applicatif sans
BYPASSRLS ; RLS forcée sur 250 tables ; 41 tables sans RLS (plateforme, référence et identité). Données existantes
cohérentes (0 écriture déséquilibrée, 0 écart de stock, 0 écart de solde), sauf l'absence de contrepartie aux soldes
d'ouverture (A-023). Détail : `06-audit-base-de-donnees.md`.

## H. Tests

| Catégorie | Contenu |
|---|---|
| Existants | 88 cas unitaires, 496 d'intégration, 194 d'API, 275 Vitest, 16 tests E2E |
| Réussis | unitaires 88/88, intégration 496/496 (relancés seuls), API 190/194, Vitest 275/275 (relancés seuls), typecheck, lint, build de HEAD |
| Échoués | build de la copie de travail (A-001) ; 4 tests d'API, dont 2 artefacts de l'audit dus à T-002, 1 délai sous charge, 1 erreur 500 non reproduite |
| Bloqués par l'environnement | E2E (Docker arrêté) ; premiers passages intégration et Vitest (charge machine) |
| Manquants | voir `08-tests-et-resultats.md` §5 (caisse, concurrence, clôture, isolation avec le rôle réel…) |
| Incident | les tests d'API écrivent toujours dans la base de développement `nexora` (T-002) ; ≈ 626 lignes ajoutées pendant l'audit, non nettoyées |

## I. Flux métier

| Flux | Étapes reliées | Étapes défaillantes ou non reliées | Vérification |
|---|---|---|---|
| **A. Vente en caisse** : panier → validation → transaction → paiements → stock → rapports → trésorerie → reçu | Tout est dans une seule transaction (`SalesService.CheckoutAsync`) : vente, lignes, mouvements de stock, paiements, créance, écriture, transaction de trésorerie, mouvement de session | Prix : listes ignorées (A-013), coupon multiplié (A-012) ; date fournie par le client (A-003) ; solde de trésorerie exposé à A-018 ; rapports « semaine » vides le dimanche (A-008) ; reçu non exécuté par l'audit | Exécuté (vente nominale) |
| **A bis. Retour** | Retour, réintégration de stock, écriture | **Le remboursement espèces n'atteint pas la session de caisse** (A-002) ; pas de verrou (A-005) | Exécuté |
| **B. Achat fournisseur** : commande → validation → réception → stock → dette → paiement → trésorerie → rapports | Réception avec écriture 4081 (V096), facture, rapprochement 3 voies, dette, paiement par le moteur | Âgé et prévision ignorent les avoirs (A-022) ; solde de trésorerie (A-018) | Tests d'intégration (non rejoué par l'audit) |
| **C. Dépense** : création → validation (4 yeux) → écriture → comptes → rapports | Reliées par le moteur financier | A-018 sur le compte payeur | Tests d'intégration |
| **D. Utilisateurs** : entreprise → utilisateurs → rôles → permissions → accès → restriction des données | Assistant d'installation, rôles, garde anti-élévation à l'attribution, permissions par point d'accès, RLS | Suspension/retrait sans garde (A-010) ; console `/ops/*` hors isolation (A-031) ; portée système (A-009) | Exécuté en partie (403 entreprise étrangère) |
| **E. RH / paie** : employé → département → contrat → présences et absences → calcul → validation → bulletins | Toutes présentes, paiement par le moteur financier, avances à 4 yeux | Arrondi différent du moteur (A-026) | Tests unitaires et d'intégration (non rejoué) |
| **F1. Caisse hors ligne** : file chiffrée → synchro → mêmes services | Idempotence par `operation_uuid` | Plafond de crédit hors ligne inopérant (A-016) | Tests d'intégration ; navigateur non testé |
| **F2. Session de caisse** : ouverture → apports/sorties → clôture → écart comptabilisé | Clôture avec validation d'écart par un superviseur | Apports et sorties sans écriture ni trésorerie (A-019) ; vente après clôture (A-003) | Exécuté (clôture) |
| **F3. Clôture d'exercice** : contrôles → écriture de clôture → à-nouveaux → états | Présentes | Faux dès le 2e exercice (A-020) ; bilan doublé (A-021) | Lecture |
| **F4. Multidevise** | Comptes, ventes et achats en devise, consolidation | Aucun taux saisissable (A-017) | Lecture + données |

Un flux n'est pas déclaré fonctionnel quand une étape essentielle n'est pas reliée à la suivante : c'est le cas de A bis,
F2, F3 et F4.

## J. Plan de correction

Voir `10-plan-de-correction.md` : étape 0 (A-001, T-002, T-001), puis A-018, A-002, A-003, A-031, A-009, A-021, A-020,
A-012, A-007, A-017.

## K. Conclusion

1. **Réellement opérationnel** (vérifié) : connexion, sessions et confirmation forte ; isolation par requête et RLS ;
   vente nominale en caisse avec stock, écriture et trésorerie dans une transaction ; grand livre de stock ; moteur en
   partie double équilibré ; comptes clients (versements, avoirs, annulations) ; protection SSRF des webhooks ;
   cohérence des données de recette.
2. **En apparence seulement** : listes de prix (créées mais jamais appliquées aux ventes) ; bascules de fonctionnalités
   (enregistrées, jamais lues) ; multidevise sur une installation neuve (aucun taux) ; paiement « simulation »
   (assumé) ; blocage des métriques par Caddy (contournable).
3. **Implémenté, mais non vérifié** : achats complets, devis/commandes/factures, crédit et recouvrement, paie, GED et
   signatures, groupe et consolidation, restaurant, terrain, marketplace, continuité, plateforme SaaS, caisse hors
   ligne dans un navigateur, et les fournisseurs réels (MonCash, SMTP, Twilio, FCM).
4. **Modules les plus incomplets** : caisse (retours, apports/sorties, date client), comptabilité de clôture, trésorerie
   (soldes, ouverture, taux), exploitation (isolation).
5. **Principaux risques de sécurité** : A-031 (données entre entreprises en SaaS), A-003 (fraude de caisse), A-009
   (RLS contournable par le code applicatif), A-010 (suspension de la direction), A-032, A-024.
6. **Cohérence des données** : sur les données existantes, oui (contrôles SELECT à zéro écart). En exploitation, non
   garantie : chaque remboursement espèces en session (A-002), chaque opération concurrente sur un compte (A-018),
   chaque inventaire tenu magasin ouvert (A-007) et la génération des à-nouveaux (A-021) créent des incohérences
   démontrées.
7. **Poursuite sans refonte** : oui. Les défauts sont localisés. Deux chantiers transverses sont nécessaires : le modèle
   de portée système et de rôle de plateforme, et la liaison caisse ↔ trésorerie ↔ comptabilité.
8. **Dix corrections les plus importantes** : A-031, A-002, A-018, A-021, A-003, A-020, A-012, A-007, A-017, A-009
   (précédées de l'étape 0 : A-001, T-002, T-001).
9. **Tests à exécuter avant de considérer l'application fiable** : suite d'intégration avec le rôle `nexora_app` ;
   tests du §5 de 08 (retour espèces + clôture, vente en session close, concurrence sur un compte, inventaire pendant
   les ventes, deux exercices clôturés, isolation de `/ops/*` entre deux clients) ; suite E2E sur installation neuve ;
   recette avec comptes bac à sable des fournisseurs ; test de charge ; test d'intrusion externe.
10. **Ordre d'exécution** : étape 0 → intégrité financière de la caisse et de la trésorerie (A-018, A-002, A-003) →
    isolation (A-031, A-009) → comptabilité de clôture (A-021, A-020) → prix et stock (A-012, A-007) → multidevise
    (A-017) → le reste par priorité, puis recette complète.
