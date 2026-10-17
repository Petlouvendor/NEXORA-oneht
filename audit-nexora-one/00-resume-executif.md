# 00 — Résumé exécutif : audit technique de NEXORA ONE (édition 2)

> Audit des 16 et 17 octobre 2026, branche `develop`, HEAD `6a47c6d`, sur le poste de développement. L'édition 1
> (HEAD `e5e243d`, 13-14 octobre) reste dans l'historique Git (commit `ee4f025`). Méthode : lecture du code (206 fichiers
> ligne à ligne, lecture ciblée des parties à risque sur une centaine d'autres), contrôles automatisés sur le reste,
> exécution de toutes les suites de tests, scénarios exécutés contre l'API locale de HEAD sur une base jetable clonée,
> requêtes SELECT en lecture seule sur `nexora_recette`. **Aucune correction, aucune migration, aucune écriture dans
> `nexora` ni `nexora_recette`.** Détails : fichiers 01 à 11 de ce dossier.

## A. État général

Depuis l'édition 1, **29 des 35 constats (A-001 à A-032, T-001, T-002, P-001) ont été corrigés** et les corrections
tiennent à la revérification (exécution ou lecture) ; 5 sont partiellement corrigés (A-003, A-009, A-011, A-022, T-001),
1 est clos par décision (A-026). L'édition 2
trouve **20 nouvelles anomalies** et un nouveau défaut de test, dont trois critiques, toutes dans des zones que
l'édition 1 n'avait pas lues ligne à ligne ou pas exécutées : extournes dans les états, multidevise, cycle comptable
pluriannuel, facturation plateforme.

- **Ossature confirmée** : 739 appels du frontend, tous résolus sur une route existante ; transaction unique par
  opération ; moteur en partie double équilibré (2 586 écritures de recette) ; stock = Σ mouvements ; sessions et
  confirmation forte solides ; RLS forcée sur 251 tables avec contexte réappliqué.
- **Tests** : build, 103 unitaires, 532 d'intégration, 200 d'API, 279 Vitest, typecheck, lint, build frontend — **tous
  verts** (avec un délai de clonage allongé, T-003). Scan de vulnérabilités .NET **rouge** (A-036). E2E non exécutés.
- **Risque daté** : **à partir du 1er janvier 2027, toute vente sera refusée** sur une installation existante, faute de
  moyen de créer l'exercice 2027 (A-042). C'est la première correction à faire.

| Indicateur | Valeur |
| --- | --- |
| Anomalies ouvertes | 3 CRITIQUES (A-043, A-052, A-049 — cette dernière seulement sur une plateforme partagée), 5 ÉLEVÉES, 15 MOYENNES, 2 FAIBLES, plus 90 constats FAIBLES par fichier |
| Matrice des fonctionnalités (04) | 51 lignes : 16 FONCTIONNEL VÉRIFIÉ, 13 DÉFECTUEUX, 14 IMPLÉMENTÉ (non vérifié ou avec réserve), 3 PARTIELLEMENT IMPLÉMENTÉ, 2 SIMULÉ, 1 PRÉSENT MAIS NON CONNECTÉ, 1 NON VÉRIFIABLE, 1 ABSENT |
| Fichiers | 1 148 suivis par Git ; 863 à examiner en éd. 2 ; 134 verdicts éd. 1 reportés (fichiers inchangés) ; 151 documents ou binaires |

**Pas de pourcentage global** : les comptes ci-dessus sont des dénombrements, pas des notes.

## B. Architecture réelle

Monolithe modulaire .NET 10 en couches sur PostgreSQL 18, SPA React 19. La logique métier vit surtout dans
57 700 lignes de services SQL de l'infrastructure. 80 contrôleurs, 1 032 points d'accès, 291 tables, 102 migrations.
Ossature inchangée ; chantiers transverses de l'éd. 1 traités. Détail : `01-architecture-reelle.md`.

## C. Inventaire et couverture

| Mode d'examen | Fichiers |
| --- | --- |
| Lecture ligne à ligne | 210 |
| Lecture ciblée (risques, routes, permissions, filtres) | 94 |
| Relu (différences depuis l'éd. 1) | 8 |
| Parcouru (interfaces, DTO) | 89 |
| Exécuté via les suites de tests | 176 |
| Contrôles automatisés (code, migrations) | 290 |
| Verdict de l'éd. 1 reporté (fichier inchangé) | 132 |
| Documentation, binaires, générés | 149 |

Registre : `02-inventaire-fichiers.csv` (une ligne par fichier, colonne `edition_du_verdict`) et
`03-audit-fichier-par-fichier.md`. Les fichiers « contrôles automatisés » et « exécutés via les tests » n'ont **pas** été
relus ligne à ligne ; le registre le dit pour chacun.

## D. État des modules

Voir `04-matrice-fonctionnalites.md`. Modules les plus touchés : comptabilité (A-042, A-043, A-044), multidevise
(A-041, A-046, A-052), caisse (A-040, A-003 résidu), pilotage (A-046, A-047, A-048, A-022 résidu).

## E. Anomalies ouvertes (détail : `09-anomalies-et-priorites.md`)

| ID | Gravité | Résumé | Preuve |
| --- | --- | --- | --- |
| A-043 | CRITIQUE | Après une extourne, balance, bilan, résultat et clôture comptent l'extourne mais plus l'origine (5711 : 123 255,90 → 124 255,90 → 122 255,90 au lieu de 123 255,90) | Exécuté |
| A-052 | CRITIQUE | Coût des ventes d'une vente en devise multiplié par le taux : 40 500 HTG au lieu de 300 ; explique l'écart stock / grand livre de la Pharmacie | Données (2 ventes) |
| A-049 | CRITIQUE (SaaS) | Webhook de facturation plateforme anonyme accepté avec le jeton par défaut public ; facture soldée quel que soit le montant | Exécuté |
| A-042 | ÉLEVÉ (échéance 2027-01-01) | Aucun moyen de créer l'exercice suivant : écritures et ventes refusées au 1er janvier | Lecture + données |
| A-040 | ÉLEVÉ | Le caissier choisit la taxe de la ligne : 143 → 130 | Exécuté |
| A-038 | ÉLEVÉ | Réceptions de transfert concurrentes : 1 unité expédiée, 7 à 8 reçues | Exécuté |
| A-041 | ÉLEVÉ | Arrondis séparés en devise : écritures refusées (409, message PostgreSQL brut) | Exécuté |
| A-003 (résidu) | ÉLEVÉ | Vente ajoutée à une session close par `/sync` depuis un appareil auto-enrôlé | Exécuté |

Plus 15 MOYENNES (A-033 à A-037, A-039, A-044 à A-048, A-050, A-051, A-009 résidu, A-022 résidu) et 2 FAIBLES
(A-011 résidu, T-003).

## F. API

1 032 points d'accès ; 18 anonymes justifiés (dont `/metrics`, désormais protégé) sauf le webhook de facturation
(A-049) ; 957 avec permission explicite ; 41 à confirmation forte ; 54 avec clé d'idempotence ; 71 concernés par une
anomalie ouverte ; doublons sans écran (`/finance/*`, `/intelligence/forecasts`) à l'origine de A-045 et A-048.
Détail : `05-audit-api.md`.

## G. Base de données

291 tables, 476 déclencheurs, 79 fonctions, 102 migrations appliquées (rejouées sur base vierge par les tests) ; rôle
applicatif sans BYPASSRLS ; RLS forcée sur 251 tables ; `app_user`, `user_role`, `access_scope` toujours sans RLS.
Données de recette : partie double équilibrée, stock = Σ mouvements, mais compte de stock de la Pharmacie à 61 696,20
pour une valorisation de 140 285,00 (A-052 pour −80 400) et un seul exercice (2026). Détail : `06-audit-base-de-donnees.md`.

## H. Tests

| Catégorie | Contenu |
| --- | --- |
| Réussis | build ; unitaires 103/103 ; intégration 532/532 (délai de clonage allongé) ; API 200/200 ; Vitest 279/279 ; typecheck, lint, build frontend ; `npm audit` 0 |
| Échoués | scan .NET : `System.Security.Cryptography.Pkcs` 10.0.0 (A-036) ; intégration au premier passage (délai de 30 s, T-003) |
| Non exécutés | E2E Playwright ; fournisseurs réels ; charge ; intrusion |
| Scénarios de l'audit | 10 exécutés, 9 anomalies confirmées (`08-tests-et-resultats.md` §3) |
| Écritures de l'audit | bases jetables seulement (fixtures, `nexora_api_it`, `nexora_audit_probe` et `nexora_audit_ed2` supprimées) |

## I. Flux métier

| Flux | Relié | Défaillant | Vérification |
| --- | --- | --- | --- |
| A. Vente en caisse | vente, stock, paiements, écriture, trésorerie, session dans une transaction ; date serveur ; coupon et prix corrigés | taxe choisie par le client (A-040) ; coût des ventes en devise (A-052) ; arrondis en devise (A-041) | Exécuté |
| A bis. Retour | remboursement espèces relié à la session (A-002 corrigé) | coût des ventes en devise (A-052) | Tests + lecture |
| B. Achat | réception avec écriture, facture, 3 voies, dette, paiement | écarts de prix capitalisés au stock (FAIBLE) ; âgé du pilotage sans avoirs (A-022 r.) | Tests d'intégration |
| C. Dépense | 4 yeux par le service | contournable par `/finance/expenses` (A-045) | Exécuté |
| D. Utilisateurs et droits | rôles, garde anti-élévation, suspension gardée, RLS | périmètre (A-033, A-034), confidentialité (A-048), révocation d'urgence (A-035) | Exécuté en partie |
| E. RH / paie | complet, moteur financier | avance versée sans confirmation forte (FAIBLE) | Tests |
| F1. Hors ligne | idempotence, plafond de crédit (A-016 corrigé) | date et session de l'appareil acceptées (A-003 r.) | Exécuté |
| F2. Session de caisse | apports et sorties comptabilisés (A-019 corrigé) | session non exigée par défaut (FAIBLE) | Tests |
| F3. Clôture et exercices | clôture et à-nouveaux corrigés (A-020, A-021) | exercice suivant impossible (A-042) ; extournes (A-043) ; réouverture (A-044) | Exécuté + lecture |
| F4. Multidevise | taux saisissables (A-017 corrigé) | A-041, A-046, A-052 | Exécuté + données |

Ne sont pas déclarés fonctionnels : A (taxe, devise), C (contournement), F3 et F4.

## J. Plan de correction

`10-plan-de-correction.md` : A-042 d'abord (échéance), puis A-052 et A-043 (chiffres comptables), A-040, A-038, A-003
résidu (fraude et stock), A-041, A-049, puis permissions (A-045, A-048, A-033, A-034) et le reste.

## K. Conclusion

1. **Réellement opérationnel** (vérifié) : authentification, sessions, MFA, confirmation forte ; isolation par requête
   et RLS ; vente en caisse en monnaie locale ; retours ; stock ; moteur en partie double ; comptes clients ;
   transferts de trésorerie ; installation sur base vierge.
2. **En apparence seulement** : états comptables après une extourne (A-043) ; KPI de trésorerie multidevise (A-046) ;
   « retour arrière » de configuration (A-037) ; sauvegardes « vérifiées » (A-051) ; paiement « simulation » (déclaré).
3. **Implémenté, non vérifié** : achats complets dans un navigateur, GED et signatures, groupe et consolidation,
   restaurant, terrain, marketplace, caisse hors ligne dans un navigateur, fournisseurs réels (MonCash, SMTP, Twilio, FCM).
4. **Modules les plus fragiles** : comptabilité (exercices, extournes), multidevise, pilotage.
5. **Principaux risques de sécurité** : A-049 (si la plateforme est exposée), A-040 et A-003 résidu (fraude de caisse),
   A-045 (contournement des 4 yeux), A-048 (confidentialité), A-036 (dépendance vulnérable).
6. **Cohérence des données** : partie double et stock cohérents ; grand livre faux dès qu'une vente se fait en devise
   (A-052) ou qu'une écriture est extournée (A-043).
7. **Poursuite sans refonte** : oui ; défauts localisés.
8. **Dix corrections prioritaires** : A-042, A-052, A-043, A-040, A-038, A-003 résidu, A-041, A-049, A-045, A-048.
9. **Tests à ajouter avant de considérer l'application fiable** : extourne puis états ; écriture au premier jour d'un
   exercice non créé ; vente, retour et facture en USD à taux 132,5 ; `taxId` client ; `/sync` en session close ;
   réceptions concurrentes ; autorisations par rôle sur `/finance/*`, `/intelligence/*`, `/users/*` ; démarrage en
   Production sans jeton de webhook ; puis E2E sur installation neuve, fournisseurs en bac à sable, charge, intrusion.
10. **Ordre d'exécution** : A-042 avant le 2026-12-31 → A-052, A-043 → A-040, A-038, A-003 r. → A-041 → A-049 →
    A-045, A-048, A-033, A-034 → A-036 → le reste par gravité, puis recette complète.
