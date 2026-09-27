# NEXORA ONE — MODULE — GESTION CLIENTS & COMPTES CLIENTS

Crédit • Versements • Dépenses • Historique financier • Recouvrement

Document fonctionnel et technique destiné à l'équipe de développement — THE NEXT GROUP.

> Spécification transmise par le propriétaire du produit le 2026-09-23, reproduite telle quelle.
> Mise en œuvre et écarts : [module-clients-comptes-clients-implementation.md](module-clients-comptes-clients-implementation.md).

## 1. Objet et vision

Ce module transforme la fiche client en véritable compte commercial et financier. Il doit centraliser l'identité, les achats, les ventes à crédit, les paiements multiples, les dépenses imputées, les retours, avoirs, remboursements, échéances et actions de recouvrement.

Principe : aucune opération financière ne doit être écrasée ou perdue. Chaque mouvement doit être référencé, daté, attribué à un utilisateur, rattaché au bon tenant et conservé dans l'historique.

## 2. Objectifs

- Centraliser toutes les informations client.
- Gérer ventes comptant et à crédit.
- Permettre plusieurs versements partiels sur un même crédit.
- Conserver l'historique détaillé de chaque versement.
- Gérer toutes les dépenses imputées à un client.
- Calculer automatiquement encours, total payé, solde et crédit disponible.
- Gérer retours, avoirs et remboursements avec traçabilité.
- Gérer limites, échéances et recouvrement.
- Fournir rapports et tableaux de bord.
- Alimenter NEXORA ONE Intelligence sans laisser l'IA devenir l'autorité financière.

## 3. Périmètre

| Domaine | Fonctions |
|---|---|
| Client | Création, recherche, modification, statut, coordonnées. |
| Achats | Historique des tickets/factures, comptant, crédit. |
| Crédit | Limite, encours, échéance, statut, conditions. |
| Versements | Paiements multiples, partiels, moyens de paiement, reçus. |
| Dépenses | Charges imputées, catégories, justificatifs, validation. |
| Retours/Avoirs | Demandes, validation, remboursement ou avoir. |
| Recouvrement | Créances échues, rappels, promesses de paiement, suivi. |
| Historique | Timeline complète et journal financier. |
| Rapports | Soldes, encours, paiements, dépenses, aging. |
| Audit | Qui, quoi, quand, montant, motif, avant/après. |

## 4. Principes métier obligatoires

- Backend autoritaire pour montants, permissions, crédits et soldes.
- Le frontend n'est jamais une source de vérité financière.
- Une transaction finalisée n'est pas supprimée pour corriger une erreur.
- Une correction crée une opération compensatoire/annulation traçable.
- Chaque versement est une écriture distincte.
- Un crédit accepte plusieurs versements.
- Le solde est recalculé côté serveur.
- Une dépense client possède catégorie, montant, motif, référence et statut.
- Toutes les opérations sont isolées par entreprise/tenant.
- Les données sensibles sont filtrées selon le rôle.

## 5. Architecture

React + TypeScript → ASP.NET Core/C# → PostgreSQL. Flux : Utilisateur → Authentification → Tenant → Rôle → Permission → API → Validation métier → Transaction PostgreSQL → Audit → Reporting/Intelligence.

Le module doit s'intégrer au POS, stock, finance, notifications, rapports, audit et moteur Intelligence.

## 6. Fiche client

| Bloc | Données |
|---|---|
| Identité | ID, code client, nom, prénom/raison sociale, type. |
| Contact | Téléphone, email, adresse, ville, pays, contact secondaire. |
| Commercial | Groupe, segment, responsable, statut. |
| Crédit | Limite, durée, conditions, échéance par défaut. |
| Financier | Encours, total dû, total payé, dépenses, avoirs, solde. |
| Documents | Factures, reçus, justificatifs autorisés. |
| Métadonnées | Tenant, succursale, créé par, dates de création/modification. |

## 7. Compte financier client

Chaque client possède un compte permettant de reconstruire son solde à partir des mouvements. Un simple champ 'solde' ne suffit pas.

| Mouvement | Effet |
|---|---|
| Vente à crédit | Augmente la dette. |
| Dépense imputée | Augmente la dette selon la politique. |
| Versement | Diminue la dette. |
| Avoir | Diminue la dette ou crée un crédit disponible. |
| Remboursement | Diminue un avoir/crédit disponible et génère un décaissement. |
| Annulation validée | Crée une écriture inverse sans supprimer l'original. |

## 8. Gestion des crédits

- Numéro de crédit unique.
- Client et vente/facture d'origine.
- Montant initial.
- Montant payé.
- Solde restant.
- Date de création.
- Échéance ou calendrier.
- Limite applicable.
- Statut : ouvert, partiel, payé, en retard, bloqué, annulé.
- Utilisateur, caisse et succursale d'origine.

| Opération | Montant |
|---|---|
| Crédit initial | 50 000 HTG |
| Versement 1 | 10 000 HTG |
| Versement 2 | 15 000 HTG |
| Versement 3 | 5 000 HTG |
| Total versé | 30 000 HTG |
| Solde | 20 000 HTG |

## 9. Versements multiples et historique

Chaque versement est une transaction indépendante. Le système ne remplace jamais le précédent montant.

| Champ | Règle |
|---|---|
| payment_id | Identifiant unique obligatoire. |
| credit_id | Crédit concerné. |
| customer_id | Client concerné. |
| amount | Montant validé côté serveur. |
| payment_method | Cash, carte, virement, mobile money, etc. |
| payment_date | Date/heure effective. |
| reference | Référence unique. |
| cash_session_id | Session de caisse si applicable. |
| received_by | Utilisateur. |
| note | Motif/commentaire selon politique. |
| status | En attente, validé, annulé. |

Option : un versement peut être affecté à plusieurs dettes. Dans ce cas, utiliser une table d'affectation afin de conserver précisément la répartition.

## 10. Dépenses imputées au client

Les dépenses liées à un client doivent être distinctes des dépenses générales de l'entreprise.

| Champ | Exemple |
|---|---|
| Référence | EXP-CLI-000045 |
| Client | CLI-000245 |
| Catégorie | Livraison |
| Description | Livraison spéciale |
| Montant | 2 000 HTG |
| Date | 23/09/2026 |
| Statut | Brouillon / En attente / Validée / Annulée |
| Justificatif | Photo/PDF/reçu |
| Créé/validé par | Utilisateur / Manager/Comptable |

Une dépense validée qui affecte le compte client ne doit pas être supprimée physiquement.

## 11. Retours, avoirs et remboursements

- Retour relié à la vente originale lorsque possible.
- Quantité retournée ≤ quantité achetée − quantité déjà retournée.
- Montant remboursable calculé côté serveur.
- Le caissier ne saisit pas librement le montant du remboursement.
- Avoir possible selon politique.
- Audit obligatoire.
- Retour nécessitant validation Manager selon les règles POS déjà définies.

## 12. Limite de crédit

- Vérifier client actif/non bloqué.
- Vérifier crédit autorisé.
- Calculer encours actuel.
- Ajouter la nouvelle dette potentielle.
- Comparer à la limite.
- Contrôler les échéances impayées.
- Appliquer les permissions du rôle.
- Un dépassement exceptionnel doit passer par le workflow d'approbation configuré.

## 13. Échéances et recouvrement

- Identifier automatiquement les créances échues.
- Calculer l'âge des créances.
- Séparer à échoir et échues.
- Permettre rappels et notes.
- Enregistrer les promesses de paiement.
- Conserver les actions de recouvrement.
- Produire une liste de suivi.

| Tranche | Définition |
|---|---|
| À échoir | Échéance non dépassée |
| 1–30 jours | Retard de 1 à 30 jours |
| 31–60 jours | Retard de 31 à 60 jours |
| 61–90 jours | Retard de 61 à 90 jours |
| > 90 jours | Retard supérieur à 90 jours |

## 14. Historique client

- Création/modification.
- Achats.
- Crédits.
- Chaque versement.
- Dépenses imputées.
- Retours.
- Avoirs.
- Remboursements.
- Blocages/déblocages.
- Changements de limite.
- Actions de recouvrement.
- Documents générés.

## 15. Tableau de bord client

| Indicateur | Contenu |
|---|---|
| Total achats | Cumul sur période. |
| Achats à crédit | Ventes financées à crédit. |
| Total payé | Paiements validés. |
| Solde dû | Montant restant. |
| Crédit disponible | Limite moins encours selon règles. |
| Dépenses imputées | Total validé. |
| Retours | Montant/quantité. |
| Avoir disponible | Crédit utilisable. |
| Créances échues | Part arrivée à échéance. |
| Dernier paiement | Date + montant. |

## 16. Écrans à développer

- Liste clients.
- Création/modification client.
- Fiche client — Vue générale.
- Achats.
- Crédits.
- Versements.
- Dépenses.
- Retours/Avoirs/Remboursements.
- Historique.
- Enregistrement d'un versement.
- Création d'une dépense.
- Créances/recouvrement.
- Rapports clients.

## 17. Recherche client

Recherche progressive par nom, prénom, téléphone, email, code client ou référence. Dans le POS, seuls les champs nécessaires doivent être exposés. Ne pas charger inutilement toute la base dans le navigateur.

## 18. Permissions

| Action | Caissier | Manager | Comptable | Direction/Admin |
|---|---|---|---|---|
| Rechercher client | Oui | Oui | Oui | Oui |
| Créer client | Selon permission | Oui | Oui | Oui |
| Modifier données | Limité | Oui | Oui | Oui |
| Voir détail financier global | Non | Oui | Oui | Oui |
| Enregistrer versement | Si autorisé | Oui | Oui | Oui |
| Annuler versement | Non/validation | Selon permission | Selon permission | Oui |
| Créer dépense | Selon permission | Oui | Oui | Oui |
| Valider dépense | Non | Oui | Oui | Oui |
| Modifier limite | Non | Selon permission | Selon permission | Oui |
| Rapports créances | Non | Oui | Oui | Oui |
| Recouvrement | Limité | Oui | Oui | Oui |
| Paramètres crédit | Non | Selon permission | Oui | Oui |

## 19. Règles spécifiques au caissier

- Ne voit pas marges, coûts d'achat ou finances globales.
- Accède uniquement aux données client nécessaires à l'opération.
- Ne modifie pas directement le solde.
- Ne supprime pas un paiement validé.
- Ne modifie pas une limite de crédit sans permission.
- Ne s'auto-approuve pas une opération nécessitant Manager.
- Les remboursements/remises exceptionnelles suivent les permissions.

## 20. Modèle PostgreSQL proposé

| Table | Rôle |
|---|---|
| customers | Identité client. |
| customer_accounts | Compte financier. |
| customer_credits | Crédits. |
| credit_installments | Échéances. |
| customer_payments | Versements. |
| payment_allocations | Affectation des paiements. |
| customer_expenses | Dépenses imputées. |
| customer_ledger_entries | Journal financier. |
| customer_returns | Retours. |
| customer_credit_memos | Avoirs. |
| customer_refunds | Remboursements. |
| collection_actions | Recouvrement. |
| customer_documents | Documents. |
| audit_logs | Audit. |

## 21. Contraintes techniques

- Utiliser un type monétaire numérique exact, jamais float.
- Devise explicite.
- Foreign keys et contraintes d'intégrité.
- Index sur tenant_id, customer_id, dates et statuts.
- Transactions PostgreSQL pour opérations multi-écritures.
- Empêcher les allocations supérieures au montant disponible.
- Conserver les mouvements financiers même si le client est archivé.

## 22. API indicative

| Méthode | Endpoint | Fonction |
|---|---|---|
| GET | /api/customers | Recherche/liste autorisée. |
| POST | /api/customers | Créer client. |
| GET | /api/customers/{id} | Fiche autorisée. |
| PATCH | /api/customers/{id} | Modifier. |
| GET | /api/customers/{id}/ledger | Journal. |
| GET | /api/customers/{id}/credits | Crédits. |
| POST | /api/customers/{id}/credits | Créer crédit. |
| POST | /api/credits/{id}/payments | Versement. |
| GET | /api/credits/{id}/payments | Historique versements. |
| POST | /api/customers/{id}/expenses | Dépense. |
| POST | /api/customer-expenses/{id}/approve | Validation. |
| POST | /api/payments/{id}/void | Annulation contrôlée. |
| GET | /api/collections | Créances/recouvrement. |

## 23. Sécurité et idempotence

- Vérifier tenant, rôle et permission côté serveur.
- Vérifier que crédit et client appartiennent au même tenant.
- Recalculer les soldes côté serveur.
- Empêcher IDOR/BOLA.
- Ne jamais accepter rôle/permission depuis le frontend.
- Utiliser une idempotency key pour les paiements sensibles.
- Empêcher double paiement lors des retries réseau.
- Auditer toute modification financière.

## 24. Mode offline POS

Les opérations offline autorisées sont mises en file locale puis synchronisées. Le serveur doit utiliser l'idempotence, vérifier les conflits et conserver les identifiants d'origine. Les opérations sensibles liées au crédit peuvent être restreintes hors ligne selon la politique de risque.

## 25. Notifications

- Crédit proche de la limite.
- Crédit dépassé.
- Créance échue.
- Paiement reçu.
- Paiement annulé.
- Dépense en attente de validation.
- Retour en attente.
- Promesse de paiement échue.

## 26. Rapports

- Clients avec solde.
- Encours total.
- Crédits ouverts.
- Paiements par période et moyen.
- Dépenses par client.
- Créances aging.
- Clients dépassant leur limite.
- Historique complet.
- Avoirs/remboursements.
- Recouvrement.
- Exports selon permissions.

## 27. NEXORA ONE Intelligence

- Détecter hausse inhabituelle du crédit.
- Repérer échéances proches.
- Identifier retards récurrents.
- Proposer des suivis de recouvrement selon règles explicables.
- Détecter anomalies de versements.
- Analyser évolution des achats.
- Répondre à « Combien ce client me doit-il aujourd'hui ? ».

Les recommandations doivent montrer leurs données et règles. L'IA ne calcule pas seule le solde et n'exécute pas silencieusement une opération financière.

## 28. Exemple complet

Vente à crédit : 50 000 HTG → paiement 10 000 → paiement 15 000 → dépense livraison validée 2 000 → solde = 27 000 HTG → paiement final 27 000 → crédit soldé. Toutes les opérations restent visibles dans le ledger et la timeline.

## 29. États métier

| Objet | États |
|---|---|
| Client | Actif, inactif, bloqué, archivé. |
| Crédit | Ouvert, partiel, payé, en retard, bloqué, annulé. |
| Paiement | En attente, validé, annulé. |
| Dépense | Brouillon, en attente, validée, rejetée, annulée. |
| Avoir | Disponible, partiellement utilisé, utilisé, annulé. |
| Remboursement | Demandé, approuvé, exécuté, rejeté, annulé. |

## 30. Audit obligatoire

- Utilisateur.
- Date/heure.
- Tenant/entreprise.
- Succursale/caisse.
- Action.
- Objet et identifiant.
- Ancienne/nouvelle valeur si pertinent.
- Montant.
- Référence.
- Motif.
- Contexte technique selon politique.

## 31. Tests d'acceptation

1. Créer un client et son compte financier.
2. Créer une vente à crédit et vérifier l'augmentation de dette.
3. Effectuer trois paiements partiels et vérifier le cumul.
4. Vérifier le refus d'un paiement supérieur au solde selon la règle configurée.
5. Envoyer deux fois la même requête et vérifier qu'un seul paiement est créé.
6. Vérifier l'historique détaillé de chaque paiement.
7. Créer une dépense client et vérifier son impact.
8. Annuler une opération autorisée et vérifier la contre-écriture/audit.
9. Tester limite de crédit et dépassement.
10. Tester échéance et aging.
11. Tester retour avec validation Manager.
12. Tester les permissions du caissier.
13. Tester isolation entre deux entreprises.
14. Tester accès API avec identifiant d'un autre tenant.
15. Tester synchronisation offline.
16. Tester rapports et exports.

## 32. Definition of Done

- Frontend terminé.
- API sécurisée.
- Migrations PostgreSQL finalisées.
- Calculs financiers côté serveur.
- Permissions testées.
- Audit opérationnel.
- Tests unitaires/intégration/sécurité.
- Paiements multiples testés.
- Retours/avoirs/remboursements testés.
- Rapports validés.
- Documentation API/métier.
- Aucun bug critique ouvert.

## 33. Interdictions développeur

- Ne pas conserver seulement un solde sans mouvements.
- Ne pas supprimer un mouvement financier finalisé.
- Ne pas faire confiance aux montants frontend.
- Ne pas exposer les données non nécessaires au caissier.
- Ne pas contourner les validations.
- Ne pas mélanger deux tenants.
- Ne pas charger toute la base client dans le navigateur sans nécessité.
- Ne pas utiliser float pour l'argent.
- Ne pas créer de double paiement lors d'un retry.

## 34. Intégrations

| Module | Intégration |
|---|---|
| POS | Vente, client, crédit, paiement, ticket, caisse. |
| Stock | Produits vendus et retours. |
| Finance | Encaissements, remboursements, créances, écritures selon architecture. |
| Notifications | Échéances, validations, recouvrement. |
| Reports | Clients, créances, paiements. |
| Intelligence | Analyses et anomalies. |
| Audit | Traçabilité. |
| Rôles | Contrôle d'accès. |

## 35. Flux global

CLIENT → ACHAT → COMPTANT OU CRÉDIT → CRÉDIT → VERSEMENTS MULTIPLES → DÉPENSES / AVOIRS / RETOURS → SOLDE RECALCULÉ → RECOUVREMENT → HISTORIQUE → AUDIT → ANALYSE

## 36. Critère final de qualité

Un gestionnaire doit pouvoir ouvrir la fiche d'un client et comprendre sans ambiguïté ce que le client a acheté, ce qu'il devait, chaque versement effectué, ce qui reste dû, toutes les dépenses imputées, les retours/avoirs/remboursements et l'utilisateur responsable de chaque opération.

---

Document de référence — NEXORA ONE / THE NEXT GROUP. Les noms exacts des tables et endpoints peuvent être adaptés à l'architecture finale ; les règles métier, de sécurité, de traçabilité et d'intégrité financière doivent être conservées.
