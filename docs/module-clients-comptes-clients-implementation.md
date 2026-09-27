# Module « Gestion clients & comptes clients » — mise en œuvre

Spécification : [module-clients-comptes-clients.md](module-clients-comptes-clients.md) (reproduite telle quelle).
Migrations : `database/migrations/V073__customer_accounts.sql` (module, 2026-09-23) et
`V074__customer_accounts_gaps.sql` (correction des écarts, 2026-09-24 — voir §6).

## 1. Principe retenu : étendre l'existant, ne rien dupliquer

Une bonne partie du module existait déjà. Les tables proposées au §20 ont été rapprochées de
l'existant au lieu d'être recréées en parallèle, comme la spec l'autorise (« les noms exacts des
tables et endpoints peuvent être adaptés ») :

| Table proposée (§20) | Réalisation |
|---|---|
| `customers` | `customer` (V006), enrichie par V073 (§6) |
| `customer_accounts` | aucun solde stocké : le compte est **reconstruit** depuis `customer_ledger_entry` (§7, §33). `customer_account.current_balance` (V006) n'est lu nulle part. |
| `customer_credits` | `customer_receivable` (V012) : c'est déjà le crédit (montant, réglé, reste généré, échéance, statut). V073 ajoute le numéro `CRD-…`, l'origine, l'auteur et la succursale. |
| `credit_installments` | **`credit_schedule`** (échéancier versionné) + **`credit_installment`** (V074) |
| `customer_payments` | `payment` (V012) + `note`, `voided_*` (V073) |
| `payment_allocations` | `payment_allocation` (V012) + `voided_at` (V073) |
| `customer_expenses` | **`customer_expense`** (V073), distincte de `expense` (dépenses générales) |
| `customer_ledger_entries` | **`customer_ledger_entry`** (V073), append-only, alimentée par triggers |
| `customer_returns` | `sale_return` + demandes `sale_return_request` (V025, V065) |
| `customer_credit_memos` | **`customer_credit_memo`** + **`customer_credit_application`** (V073) |
| `customer_refunds` | **`customer_refund`** (V073) |
| `collection_actions` | **`collection_action`** (V073) ; promesses, litiges, blocages : V040 |
| `customer_documents` | `issued_document` (Phase 30) + GED `document` (Phase 45) ; justificatif de dépense = `customer_expense.document_id` |
| `audit_logs` | `audit_log` (immuable, V030) + `trg_audit_*` sur toutes les nouvelles tables |

## 2. Défauts réels trouvés et corrigés

L'analyse de l'existant a mis au jour 5 défauts. Ils sont corrigés et chacun est couvert par un test.

| # | Défaut | Conséquence | Correction |
|---|---|---|---|
| D1 | Un retour sur une vente à crédit créditait le compte clients en comptabilité **sans réduire la créance** (`customer_receivable`). | Le client restait débiteur du montant rendu ; grand livre et sous-livre divergeaient. | `SalesReturnService` impute d'abord le retour sur le reste dû de la vente ; le reliquat devient un **avoir disponible** (compte `store_credit`). |
| D2 | Le contrôle de crédit refusait sur des créances au statut `OVERDUE`, **statut jamais posé** par aucun code. | Un client en retard de paiement pouvait continuer d'acheter à crédit. | `SqlCreditControl` lit l'échéance (`due_date < aujourd'hui`) ; l'encours inclut les crédits en litige ; une fiche inactive ou archivée est refusée sans dérogation possible. |
| D3 | Le relevé client (`/credit/statements`) additionnait les ventes crédit/mixte au total **et** tous les paiements du client, paiements comptant du POS compris. | Un client qui paie comptant apparaissait créditeur ; les retours n'y figuraient pas. | Le relevé lit désormais le journal client. |
| D4 | Le moteur financier ne vérifiait pas qu'une créance affectée à un versement appartient au client payeur (idem dette / fournisseur). | Un versement pouvait solder la créance d'un autre client de la même entreprise. | Contrôle dans `FinancialEngine.ApplyPaymentAsync`. |
| D5 | Une vente à crédit sans échéance explicite donnait une créance **sans date d'échéance**. | Crédit jamais « échu », jamais relancé, absent de la balance âgée. | Échéance par défaut = durée de la fiche client, sinon réglage `credit.default_due_days` (30). |

Autre lacune comblée : **l'écran POS ne permettait pas de choisir un client**, donc aucune vente
à crédit n'était possible depuis la caisse alors que l'API l'acceptait.

## 3. Réalisation par section

| § | Exigence | Réalisation |
|---|---|---|
| 4, 33 | Pas de solde figé, rien supprimé, correction = contre-écriture | Journal client append-only (triggers `trg_customer_ledger_immutable`) ; versement annulé = affectations neutralisées (`voided_at`) + extourne + sortie de trésorerie ; dépense annulée = extourne + crédit `CANCELLED` ; suppression d'une dépense validée bloquée en base. |
| 6 | Fiche client | Prénom, ville, pays, contact secondaire, segment, responsable, durée et conditions de crédit, succursale, notes, créé/modifié par. `PATCH /customers/{id}` partiel ; le `PUT` historique ne peut plus effacer ces champs. Statut actif/inactif/archivé (`POST /customers/{id}/status`, motif obligatoire) ; archivage refusé si le compte n'est pas soldé. |
| 7 | Compte reconstruit depuis les mouvements | `customer_ledger_entry` alimenté **par la base** (triggers sur crédit, affectation, radiation, avoir, remboursement) : aucun chemin (POS, commandes, sync hors ligne, API finance) ne peut l'oublier. Solde = Σ débit − Σ crédit = crédits restant dus − avoirs disponibles ; la fiche affiche « journal concordant ». Historique existant repris par la migration (recette : 69 mouvements, 0 écart). |
| 8 | Crédits | Numéro unique `CRD-AAAA-NNNNNN` posé par la base ; origine (vente / dépense / reprise) ; statut affiché ouvert / partiel / payé / en retard / bloqué (litige) / annulé / radié — « en retard » dérivé de l'échéance. **Échéancier** (« échéance ou calendrier ») : N échéances égales ou lignes saisies, versionné ; l'échéance du crédit devient la première échéance non couverte (balance âgée, relances et contrôle de crédit suivent). |
| 9 | Versements multiples | Chaque versement est une opération distincte (moyen, session de caisse, référence, note, reçu par). Répartition explicite ou automatique (plus ancienne échéance d'abord) ; un versement peut solder plusieurs crédits (`payment_allocation`). Espèces encaissées à la caisse → mouvement de caisse `payin`, attendu à la clôture. |
| 10 | Dépenses imputées | Brouillon → en attente → validée (par une **autre** personne, contrainte base) / rejetée → annulée par contre-écriture. Refacturée : devient un crédit (Dr clients / Cr produits). Non refacturée : suivie sans toucher au solde. |
| 11 | Retours, avoirs, remboursements | Retour toujours validé par un Manager (V065). Imputation sur la dette de la vente puis avoir `AVC-…` ; avoir imputable sur d'autres crédits (Dr compte de l'avoir / Cr clients), **utilisable en paiement à la caisse** (du plus ancien au plus récent, sans limite de crédit requise) ou remboursable : demandé → approuvé (≠ demandeur) → décaissé (Dr avoir / Cr trésorerie). Un remboursement ne dépasse jamais le disponible, demandes en cours comprises. Retours antérieurs à V073 reconstitués en avoirs par V074. |
| 12 | Limite de crédit | Actif / non bloqué / limite > 0 / échéances impayées / encours (toutes devises, converti) + nouvelle dette ≤ limite. **Dépassement par le workflow d'approbation configuré** : demande du caissier → `approval_request` type `credit_sale` (niveaux selon `approval_policy`, demandeur ≠ approbateur, approbateurs distincts) → approbation à distance ou sur place (« Responsable présent ») → la vente de CE panier passe une fois (approbation consommée). La dérogation directe par `pos.credit_override` + motif reste possible pour son titulaire. |
| 13 | Échéances & recouvrement | Aging par tranche (existant + rapport `aging`) ; actions de recouvrement journalisées (`collection_action`) avec prochaine date ; promesses, litiges, blocages (Phase 27). |
| 14 | Historique | `GET /customers/{id}/timeline` : création et modifications de la fiche (lues dans l'audit, limite de crédit et statut avant → après), achats, crédits, chaque versement et annulation, dépenses, retours, avoirs, remboursements, blocages, promesses, litiges, actions, documents émis — avec l'auteur. |
| 15 | Tableau de bord | `GET /customers/{id}/account` : les 10 indicateurs + solde net, contrôle de concordance, compteurs en attente. |
| 16 | Écrans | Liste (écran Clients & fournisseurs, lien vers la fiche) ; fiche `/customers/:id` avec 8 onglets ; dialogues versement, dépense, remboursement, décaissement ; annulation motivée ; Crédit & recouvrement += **Validations** et **Rapports clients** ; POS : client + vente à crédit + versement. |
| 17 | Recherche | `GET /customers/lookup` : 2 caractères minimum, 20 résultats au plus ; nom/prénom normalisés (index trigramme), code, téléphone (chiffres), email, n° de crédit ou de ticket. Au POS : code, nom, 4 derniers chiffres du téléphone, crédit disponible (si `pos.view_customer_balance`). |
| 18-19 | Permissions / caissier | Voir §4 ci-dessous. |
| 21 | Contraintes techniques | `money_amount numeric(19,4)`, devise explicite partout, FK composites (entreprise), index entreprise/client/date/statut, transactions PostgreSQL, allocations et utilisations bornées en base. **Jamais deux devises additionnées** : fiche en devise du client + solde par devise, journal par devise, totaux de rapports par devise ; encours converti au dernier taux (`nexora.convert_amount`, même règle que le moteur financier) pour la limite ; taux manquant → crédit refusé. |
| 22 | API | Voir §5, y compris `POST /customers/{id}/credits` (crédit hors vente — reprise de solde, motivé, idempotent, contrepartie en compte d'attente). |
| 23 | Sécurité & idempotence | Permission vérifiée côté serveur ; identifiant d'une autre entreprise → 404 ; `Idempotency-Key` exigée sur versement, annulation, dépense, avoir, remboursement ; rejeu du même `operationUuid` → le même versement. |
| 24 | Hors ligne | Ventes à crédit hors ligne soumises à `offline_policy.allow_offline_credit` (Phase 10, désactivé par défaut) ; rejeu d'une vente synchronisée → un seul crédit, un seul mouvement. **Versement client hors ligne** : mis en file chiffrée au poste, rejoué par le moteur de synchronisation (`customer_payment`) avec le même identifiant d'opération ; `offline_policy.allow_offline_customer_payment` ; conflit tracé si le crédit a été soldé entre-temps. |
| 25 | Notifications | Crédit proche / au-delà de la limite (mensuel), créance échue, promesse échue : job `credit.scan_alerts`, dédupliqué. Versement reçu (au responsable du client), versement annulé (détenteurs de `credit.void_payment`), dépense et remboursement à valider, décision au demandeur. |
| 26 | Rapports | 10 rapports (`/customer-reports`) : clients avec solde, crédits ouverts, balance âgée, versements par moyen, journal des versements, dépenses par client, clients au-delà de la limite, avoirs & remboursements, recouvrement, journal financier. Export CSV (UTF-8 BOM, « ; », neutralisation des formules) avec `customers.export`. |
| 27 | Intelligence | Signaux explicables sur la fiche (règle + chiffres affichés) : retards récurrents, limite proche/dépassée, hausse inhabituelle du crédit, échéances proches, annulations de versements répétées, baisse/hausse d'activité. L'assistant répond à « Combien ce client me doit-il aujourd'hui ? » (client en contexte, ou code/nom cité) à partir du compte calculé par le serveur, calcul détaillé. Aucun signal ne modifie un solde ni n'exécute d'opération. |
| 28 | Exemple complet | Reproduit à l'identique par le test d'intégration `Complete_example_…`. |
| 29 | États | Client, crédit, versement (`completed`/`void`), dépense, avoir, remboursement : tous les états de la spec. |
| 30 | Audit | `trg_audit_*` (utilisateur, date, entreprise, avant/après) ; le journal client porte en plus l'auteur, la succursale, la référence et le motif. |
| 36 | Critère final | La fiche répond aux 6 questions : achats, dû initial, chaque versement (avec l'auteur), reste dû, dépenses, retours/avoirs/remboursements. |

## 4. Permissions (§18)

Nouvelles permissions V073 : `credit.receive_payment`, `credit.void_payment`, `credit.refund`,
`customers.create_expense`, `customers.approve_expense` ; V074 : `credit.create` (crédit hors vente).

| Action | Permission | Caissier | Caissier principal | Manager | Comptable (chef) | Direction/Admin |
|---|---|---|---|---|---|---|
| Rechercher client | `customers.view` | ✓ | ✓ | ✓ | ✓ | ✓ |
| Créer client | `customers.create` | ✓ | ✓ | ✓ | — | ✓ |
| Modifier la fiche | `customers.update` | — | — | ✓ | — | ✓ |
| Détail financier global | `credit.view_portfolio` | — | — | ✓ | ✓ | ✓ |
| Enregistrer un versement | `credit.receive_payment` | à accorder | ✓ | ✓ | ✓ | ✓ |
| Annuler un versement | `credit.void_payment` | — | — | ✓ | ✓ | ✓ |
| Saisir une dépense | `customers.create_expense` | à accorder | ✓ | ✓ | ✓ | ✓ |
| Valider une dépense | `customers.approve_expense` (≠ auteur) | — | — | ✓ | ✓ | ✓ |
| Modifier limite / conditions | `credit.set_limit` | — | — | ✓ | ✓ | ✓ |
| Approuver un remboursement | `credit.refund` (≠ demandeur) | — | — | ✓ | ✓ | ✓ |
| Créer un crédit hors vente (reprise) | `credit.create` | — | — | — | ✓ | ✓ |
| Poser un échéancier | `credit.set_limit` | — | — | ✓ | ✓ | ✓ |
| Demander un dépassement au POS | `pos.sell_credit` | ✓ | ✓ | ✓ | — | ✓ |
| Approuver un dépassement (workflow) | `pos.credit_override` (≠ demandeur) | — | — | ✓ | — | ✓ |
| Rapports / export | `credit.view_portfolio` / `customers.export` | — | — | ✓ | ✓ / — | ✓ |

« Selon permission » / « si autorisé » : non accordé par défaut ; l'entreprise l'accorde par un
rôle personnalisé. Le caissier ne voit ni les soldes globaux ni les créances de la liste clients,
ne modifie aucun solde et ne valide rien lui-même.

## 5. API

| Méthode | Route | Permission |
|---|---|---|
| GET | `/customers?q=` · `/customers/{id}` | `customers.view` |
| GET | `/customers/lookup?q=` | `customers.view` |
| POST · PUT · PATCH | `/customers` · `/customers/{id}` | `customers.create` / `customers.update` |
| POST | `/customers/{id}/status` | `customers.update` |
| GET | `/customers/{id}/{account,ledger,payments,purchases,expenses,credit-memos,refunds,timeline,collection-actions}` | `credit.view_portfolio` |
| GET | `/customers/{id}/credits` | `credit.view_portfolio` ou `credit.receive_payment` |
| POST | `/customers/{id}/payments` · `/credits/{id}/payments` | `credit.receive_payment` + clé d'idempotence |
| GET | `/credits` · `/credits/{id}` · `/credits/{id}/payments` · `/payments/{id}` | `credit.view_portfolio` |
| POST | `/payments/{id}/void` | `credit.void_payment` + clé d'idempotence |
| POST | `/customers/{id}/expenses` · `/customer-expenses/{id}/{submit,cancel}` | `customers.create_expense` |
| POST | `/customer-expenses/{id}/{approve,reject}` | `customers.approve_expense` |
| GET | `/customer-expenses` · `/credit-memos` · `/customer-refunds` | `credit.view_portfolio` |
| POST | `/credit-memos/{id}/apply` · `/customer-refunds` | `credit.receive_payment` |
| POST | `/customer-refunds/{id}/{approve,reject}` | `credit.refund` |
| POST | `/customer-refunds/{id}/{execute,cancel}` | contrôlé par le service |
| GET | `/customer-payment-options` | `credit.receive_payment` (comptes et moyens, **sans solde**) |
| POST | `/customers/{id}/credits` | `credit.create` + clé d'idempotence |
| POST | `/credits/{id}/installments` | `credit.set_limit` |
| GET | `/customers/{id}/ledger?currency=USD` | `credit.view_portfolio` (une devise à la fois) |
| POST · GET | `/pos/credit-override-requests[/{id}]` | `pos.sell_credit` |
| GET · POST | `/pos/credit-override-requests` · `/{id}/{approve,reject}` | `pos.credit_override` |
| GET | `/collections` · `/collection-actions` · `/customer-reports[/{key}]` | `credit.view_portfolio` |
| GET | `/customer-reports/{key}/export` | `credit.view_portfolio` + `customers.export` |

## 6. Écarts de V073 — corrigés par V074 (2026-09-24)

À la demande du propriétaire (« les écarts doivent être corrigés, suivre la consigne du document du
module »), chaque écart assumé en V073 a été traité :

| Écart V073 | Correction V074 | Preuve (test d'intégration) |
|---|---|---|
| Pas d'échéancier (§8, §20) | `credit_schedule` + `credit_installment`, versionnés et immuables ; la prochaine échéance non couverte pilote l'échéance du crédit | `Installment_schedule_drives_the_next_due_date…` |
| Pas de création de crédit (§22) | `POST /customers/{id}/credits` : reprise de solde motivée, idempotente (`source_id` = opération), Dr clients / Cr compte d'attente, permission `credit.create` | `Opening_credit_creates_a_traced_debt_once…` |
| Avoir non utilisable au POS (§7, §15) | `CheckoutCommand.StoreCreditAmount` : imputation des avoirs (plus ancien d'abord) sur le crédit né de la vente, dans la transaction ; aucune limite de crédit requise | `Store_credit_pays_a_sale_at_checkout…` |
| Dépassement sans workflow (§12) | Demande `approval_request` `credit_sale` suivant `approval_policy` (niveaux, 4-yeux, approbateurs distincts), approbation à distance ou sur place, consommée par une seule vente | `Credit_overrun_goes_through_the_configured_approval_workflow_once`, `Double_approval_policy_requires_two_distinct_approvers` |
| Totaux multi-devises mélangés (§21) | Solde par devise, journal par devise, totaux de rapports par devise, encours converti pour la limite, refus si taux manquant | `Receivables_in_another_currency_are_converted…` |
| Versement hors ligne impossible (§24) | File chiffrée au poste → synchronisation `customer_payment`, même identifiant d'opération, politique `allow_offline_customer_payment`, conflit si plus rien n'est dû | `Offline_customer_payment_syncs_once…` |
| Retours antérieurs non repris | Avoirs `AVC-REPRISE-…` (compte clients) imputés sur la créance de la vente ; le grand livre de l'époque n'est pas réécrit | `Legacy_return_on_a_credit_sale_is_rebuilt…` |

Écrans : fiche client (bouton « Créer un crédit », échéancier dans le détail d'un crédit, soldes par
devise, journal par devise) ; POS (« Payer avec l'avoir », demande de dépassement + « Responsable
présent », versement hors ligne) ; onglet POS Validations += dépassements de crédit ; centre de
synchronisation += « Versements clients hors ligne ».

## 7. Vérification

- `V073` appliquée sur une base vide (reconstruction complète : 73 migrations, 260 tables, 221 RLS,
  57 s), sur `nexora` (test de fumée + démo OK) et sur `nexora_recette` (69 mouvements repris,
  0 écart journal ↔ créances).
- Tests : `CustomerAccountTests` (13 scénarios d'intégration : exemple §28, excédent + avoir,
  idempotence, annulation + extourne + audit + immutabilité, limite / échéance / inactif, aging,
  retour Manager + avoir + remboursement 4-yeux, annulation de dépense, isolation 2 entreprises +
  RLS, hors ligne rejoué, rapports / recherche / fiche, assistant, recouvrement) ;
  `CustomerAccountEndpointsTests` (21 cas API : refus caissier, recherche minimale, IDOR 404,
  idempotence obligatoire, export CSV) ; `customersApi.test.ts` (4) ; `PosSalesTests` adapté
  (échéance future, D2).
- Suite complète Release : 18 unitaires, 131 API, 372/375 intégration, 142 frontend (33 fichiers).
  Les 3 échecs d'intégration (`MarketplaceEcosystemTests.Usage_recorder…`,
  `GuideFonctionnelTests.A_recorded_expense…`, `GuideFonctionnelTests.The_assistant_answers…`)
  sont **antérieurs et étrangers au module** : ils comparent une date UTC à une date PostgreSQL
  locale et échouent chaque soir entre 20 h et minuit (heure d'Haïti).
- Recette navigateur (copie jetable de la base, supprimée ensuite) : 8 onglets de la fiche, versement
  par dialogue, vente à crédit complète au POS en caissier, 0 erreur console, 0 requête en échec.

**Après V074 (2026-09-24)** :

- Reconstruction complète sur base vide : 74 migrations, 262 tables, 223 tables sous RLS ; test de
  fumée + démo OK ; schéma identique (empreinte md5) à celui de `nexora` migrée pas à pas.
  `V074` appliquée à `nexora` et `nexora_recette` (0 avoir de reprise nécessaire, 0 écart
  journal ↔ créances).
- `CustomerAccountGapsTests` (8 scénarios : échéancier, crédit hors vente idempotent, avoir payant
  au POS, dépassement par workflow consommé une fois, double approbation par 2 approbateurs distincts,
  multi-devises converti et refus sans taux, versement hors ligne rejoué une fois, reprise d'un
  ancien retour) ; `CustomerAccountEndpointsTests` étendu (nouvelles routes refusées au caissier,
  clé d'idempotence du crédit hors vente) ; `customersApi.test.ts` (8).
- Suite complète Release : **18 unitaires, 135 API, 383/383 intégration, 146 frontend (33 fichiers)
  — 0 échec**.

## 8. Points ouverts

- **Fuseau horaire métier** : plusieurs calculs mélangent la date UTC de l'application et la date
  locale de PostgreSQL (cause des 3 tests ci-dessus ; l'assistant date un versement du soir au
  lendemain). À traiter globalement par un fuseau d'entreprise. (Suite verte le 2026-09-24 à
  7 h 30 ; ces 3 tests échouent toujours le soir.)
- Plus aucun écart de la spécification du module : ceux de V073 sont corrigés par V074 (§6).
