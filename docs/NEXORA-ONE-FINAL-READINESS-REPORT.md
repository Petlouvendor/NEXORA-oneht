# NEXORA ONE — FINAL READINESS REPORT

> Rapport final du plan maître « Finalisation complète du produit » (§59), à l'issue des phases 0 à 20.
> Date : 2026-10-07. Branche de référence : `develop`. Pour la direction et l'équipe qui mettra Nexora One en service.

## 1. Verdict

**Nexora One est prêt pour une mise en production pilote** (une entreprise, un ou quelques points de vente), **sous
quatre conditions** à lever avant d'ouvrir l'application aux utilisateurs :

1. **Décider du traitement comptable des réceptions non facturées** (§8.1). Aujourd'hui, une réception augmente le
   stock sans écriture : le compte 3110 est en retard sur la valeur du stock tant que les factures fournisseur ne sont
   pas saisies. C'est la seule incohérence financière révélée par la suite E2E.
2. **Configurer et tester les fournisseurs réels** : paiement MonCash ou carte, SMTP, SMS et notifications push. Le code
   est prêt et testé contre les contrats des fournisseurs, mais aucun identifiant marchand réel n'a été validé.
3. **Brancher l'acheminement des alertes vers l'astreinte** (Alertmanager : e-mail, SMS). Les 8 règles existent et ont
   été déclenchées de bout en bout, mais rien ne les envoie encore à une personne.
4. **Faire, sur l'infrastructure cible**, un exercice de restauration et une recette utilisateur signée (UAT). Les
   deux sont outillés et ont réussi sur le poste de développement.

Le multi-entreprises, le groupe, la paie, le restaurant et le terrain sont fonctionnels et testés (intégration et API),
mais n'ont pas encore de scénario E2E : à valider en recette avant de les ouvrir.

## 2. Matrice des modules

Légende : **Complet** = backend, écrans, validation et tests ; **Partiel** = utilisable, avec des manques listés.
« E2E » = couvert par un scénario de bout en bout dans le navigateur (suite Playwright).

| Module | Statut | Backend | Frontend | Base | Tests | E2E | Prêt pour la production |
|---|---|---|---|---|---|---|---|
| Catalogue, prix, promotions, unités | Complet | ✅ | ✅ | ✅ | ✅ | ✅ | Oui |
| Stock, emplacements, inventaires, transferts, lots | Complet | ✅ | ✅ | ✅ | ✅ | ✅ | Oui |
| Achats : commandes, réceptions, factures, rapprochement, retours | Complet | ✅ | ✅ | ✅ | ✅ | ✅ | **Sous réserve §8.1** |
| Ventes : devis, commandes, acomptes, factures, documents PDF | Complet | ✅ | ✅ | ✅ | ✅ | — | Oui |
| Caisse : encaissement multi-paiement, retours, remises, clôture | Complet | ✅ | ✅ | ✅ | ✅ | ✅ | Oui |
| Caisse hors ligne (profil chiffré, file, synchronisation) | Complet | ✅ | ✅ | ✅ | ✅ | ✅ | Oui |
| Clients, comptes clients, avoirs, remboursements | Complet | ✅ | ✅ | ✅ | ✅ | ✅ | Oui |
| Crédit et recouvrement (promesses, litiges, agenda) | Complet | ✅ | ✅ | ✅ | ✅ | ✅ (crédit) | Oui |
| Finance et trésorerie (comptes, transferts, dépenses, rapprochement) | Complet | ✅ | ✅ | ✅ | ✅ | — | Oui |
| Comptabilité générale (écritures, plan, périodes, états, clôture) | Complet | ✅ | ✅ | ✅ | ✅ | ✅ (cohérence) | Oui |
| Rapports, BI, objectifs, budgets, prévisions | Complet | ✅ | ✅ | ✅ | ✅ | ✅ (formulaires) | Oui |
| Intelligence (recommandations, scores, assistant déterministe) | Complet | ✅ | ✅ | ✅ | ✅ | — | Oui |
| Restaurant (salle, cuisine, recettes) | Complet | ✅ | ✅ | ✅ | ✅ | — | Après recette |
| Terrain (tournées, interventions, preuves, hors ligne) | Complet | ✅ | ✅ (web) | ✅ | ✅ | — | Après recette |
| RH et paie (cycle, avances, bulletins) | Complet | ✅ | ✅ | ✅ | ✅ | — | Après recette |
| Groupe et consolidation | Complet | ✅ | ✅ | ✅ | ✅ | — | Après recette |
| GED et signatures électroniques | Complet | ✅ | ✅ | ✅ | ✅ | — | Oui |
| Paiements et connecteurs (MonCash, carte, webhooks, banque) | Complet | ✅ | ✅ | ✅ | ✅ | — | **Sous réserve §1.2** |
| Notifications (e-mail, SMS, push, modèles, relances) | Complet | ✅ | ✅ | ✅ | ✅ | — | **Sous réserve §1.2** |
| Sécurité (sessions, MFA, confirmation forte, audit, fraude) | Complet | ✅ | ✅ | ✅ | ✅ | ✅ (alerte) | Oui |
| Administration (rôles, workflows, automatisations) | Complet | ✅ | ✅ | ✅ | ✅ | — | Oui |
| Continuité (sauvegardes, restauration, incidents, exercices) | Complet | ✅ | ✅ | ✅ | ✅ | ✅ (formulaires) | Oui (outillé) |
| Exploitation et production (sondes, migrations, état de production) | Complet | ✅ | ✅ | ✅ | ✅ | ✅ | Oui |
| Synchronisation et appareils | Complet | ✅ | ✅ | ✅ | ✅ | ✅ (hors ligne) | Oui |
| Migration de données et accueil | Complet | ✅ | ✅ | ✅ | ✅ | — | Oui |
| Secteurs et configuration | Partiel | ✅ | ⚠️ export/clonage/historique sans écran | ✅ | ✅ | — | Oui |
| Plateforme SaaS et facturation | Partiel | ✅ | ⚠️ tickets, coupons, options, licences sans écran | ✅ | ✅ | — | Pour l'éditeur seulement |
| Marketplace et API publique | Partiel | ✅ | ✅ | ✅ | ✅ | — | Après §8.4 |
| Application mobile native | Non disponible | ✅ API | ❌ (web mobile à la place) | ✅ | ✅ | — | Non (web mobile : oui) |

## 3. Fonctionnalités

**Complètes** : tout le cycle commerce (catalogue → achat → stock → vente → caisse → encaissement → comptabilité), en
ligne et hors ligne ; crédit client et recouvrement ; trésorerie et comptabilité en partie double avec clôtures ; BI ;
paie ; groupe ; GED et signature ; sécurité (jetons courts renouvelés, détection de vol de session, 41 actions critiques
à confirmation forte, double authentification) ; continuité ; exploitation ; installation, mise à jour et sauvegarde
outillées.

**Partielles** :
- tickets de support, coupons, options et licences de la plateforme : API sans écran ;
- personnalisation : filtres enregistrés et dispositions du tableau de bord prêts côté API et client, non branchés aux
  écrans ;
- configuration : export, clonage et historique de versions sans écran ;
- caisse : mise en attente d'une vente (« park ») sans écran.

**Non disponibles** : application mobile native (le web mobile hors ligne couvre le terrain et la caisse) ;
authentification par clé matérielle (WebAuthn) et SSO ; portail partenaires de la marketplace ; assistant à modèle de
langage (l'assistant est déterministe, ses réponses viennent des indicateurs calculés).

## 4. Routes backend non appelées par l'interface

Analyse statique (croisement automatique des 1 028 routes des contrôleurs avec les appels de l'interface) :
**au moins 830 routes (81 %) sont appelées par un écran**. Pour les 198 autres, l'analyse ne suit pas toutes les
compositions de chemins : certaines sont en réalité appelées. Elles se répartissent ainsi :

| Catégorie | Exemples |
|---|---|
| Alias de compatibilité (même service) | `/ops/status` (= `/system/status`), `/ops/audit`, `/security/incidents`, `/releases`, `/accounting/*` (= `/accounting-periods`, `/trial-balance`…) |
| Appels machine, sans écran par nature | webhooks entrants (paiement, facturation), `/licenses/verify`, `/mobile/*`, `/metrics`, `/finance/*` (moteur comptable pour intégrations), `/sales` (API) |
| **Sans écran, confirmées à la main** | tickets de support (`/support/tickets`, `/platform/support/tickets`), `/pos/park`, `/platform/coupons`, `/platform/addons`, licences de la plateforme |

## 5. Fonctions de l'interface jamais appelées

45 fonctions d'API exportées ne sont appelées par aucun écran (hors tests). Ce sont des capacités préparées mais pas
encore branchées, par exemple :
- filtres enregistrés et dispositions du tableau de bord (`personalizationApi`, 7) ;
- export, clonage et historique de configuration ;
- annulation d'une vente en attente ;
- import ligne par ligne ;
- certificats, contrôles de conformité et score de sécurité ;
- abonnement et paiement manuel de la plateforme ;
- anomalies et prévisions de l'intelligence.

La liste complète a été produite par l'analyse de fin de projet. À brancher ou supprimer en phase de maintenance.

## 6. Tests et couverture

| Suite | Nombre | Résultat |
|---|---|---|
| Unitaires backend | 59 | 59 / 59 |
| Intégration backend (PostgreSQL réel, RLS) | 484 | 484 / 484 |
| API backend (HTTP réel) | 194 | 194 / 194 |
| Interface (Vitest) | 274 (61 fichiers) | 274 / 274 |
| E2E Playwright (installation neuve) | 16 | 15 réussis, 1 défaut connu déclaré (§8.1) |

Couverture mesurée (lignes) :
- **backend : 83 %** (36 196 lignes sur 43 588) (unitaires, intégration et API fusionnés) ;
- **interface : 12 %** en tests unitaires. Les écrans sont vérifiés par les scénarios E2E, pas par des tests unitaires
  de composants ; 77 % des branches des modules testés sont couvertes.

Les scénarios E2E vérifient à chaque action les effets croisés stock / caisse / créances / dettes / comptabilité, et
l'absence de boîte native et d'erreur serveur.

## 7. Sécurité

Multi-entreprise isolé par RLS PostgreSQL (rôle applicatif sans BYPASSRLS, testé). Jetons d'accès de 15 minutes et
cookie de rafraîchissement HttpOnly avec rotation et détection de vol. Délai d'inactivité et durée maximale de session.
41 actions critiques soumises à confirmation forte. Double authentification TOTP, imposable par rôle. Audit
immuable. Limites de débit. En-têtes CSP et HSTS, et HTTPS de bout en bout. Secrets en fichiers, refus de démarrer
sans eux. Métriques et sondes jamais exposées sur Internet. Une alerte de sécurité (session compromise) est déclenchée
de bout en bout.

Restent : pas de WebAuthn ni de SSO ; les URL de webhooks sortants ne sont pas protégées contre la falsification de
requêtes côté serveur (SSRF) ; aucun audit de sécurité externe ni test d'intrusion n'a été fait.

## 8. Dette technique, risques et recommandations

### 8.1 Réceptions non facturées (décision demandée)
Introduire le compte 4081 « fournisseurs — factures non parvenues » : écriture à la réception, soldée par la facture,
avec écart de prix et retours. Le test E2E correspondant est prêt et redeviendra bloquant ensuite.

### 8.2 Performance
Paquet chargé au démarrage réduit de 1 441 à 479 Ko (−67 %), écrans chargés à la demande. Appels réseau en double
supprimés (tableau de bord : 25 → 18 appels). Toutes les réponses mesurées sont sous 400 ms, avec des volumes de 13 à
67 Ko par écran. **Aucun test de charge** n'a été fait (k6/JMeter) : à réaliser avant d'ouvrir plus de quelques
caisses simultanées.

### 8.3 Exploitation
- **Pas de passage à l'échelle horizontal testé** : une seule instance d'API, tâches planifiées dans le même
  processus.
- Pas de réplication PostgreSQL ni de sauvegarde continue (PITR) : la sauvegarde est quotidienne. **Une journée de
  perte de données est possible**, à répliquer hors site.
- Rotation des clés de chiffrement MFA et des données personnelles non outillée.

### 8.4 Intégrations
Protéger les webhooks sortants contre la SSRF, et émettre automatiquement les 13 événements métier encore manuels avant
d'ouvrir la marketplace à des tiers.

### 8.5 Recommandations, dans l'ordre
1. Décision §8.1, puis correction.
2. Recette utilisateur signée sur un magasin pilote, avec fournisseurs réels configurés.
3. Alertmanager et astreinte ; réplication des sauvegardes hors site.
4. Test de charge et test d'intrusion externe.
5. Scénarios E2E pour restaurant, paie, groupe et terrain.
6. Brancher ou retirer les capacités sans écran (§4, §5).

## 9. Références

- Rapports de phase : `docs/finalisation-phase-1-catalogue.md` à `docs/finalisation-phase-20-ux-performance.md`.
- Exploitation : `docs/exploitation/runbook-production.md`, `docs/exploitation/tests-e2e.md`.
