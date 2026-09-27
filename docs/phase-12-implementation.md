# NEXORA — Phase 12 — État de l'implémentation

Référence : [`phase-12-integrations-paiements-ecosysteme.md`](phase-12-integrations-paiements-ecosysteme.md).

## Principe directeur

Le cœur métier reste **indépendant du fournisseur**. Toute intégration passe par un
**connecteur** au contrat commun, possède son état, ses logs et ses références. Les webhooks
entrants sont **authentifiés (signature HMAC) et idempotents**. Une erreur externe devient une
**anomalie tracée**, jamais un échec silencieux ni une transaction interne corrompue (§2, §12-14).

## Acquis réutilisé

`bank_statement` / `bank_statement_line` (V011), `payment` + `payment_allocation` +
`financial_transaction` (V012), moteur financier `IFinanceOperations.RecordCustomerPaymentAsync`
(Phase 4), `audit_log` + `audit_row()` (V016/V030), RLS multi-tenant, `[RequirePermission]`
deny-by-default, module de permissions `integrations` déjà seedé (V021), rate limiting + TLS +
headers de sécurité (Phase 3).

## Ce qui est livré — `V031`

| Objet | Rôle |
|---|---|
| `integration_connector` | registre (§5-6) : `(entreprise, code)` unique, type `payment/bank/ecommerce/messaging/…`, `environment test/production`, `status inactive/active/error`, `config jsonb` (non secret), `webhook_secret`, `credentials_ref` (pointeur coffre — **jamais le secret en clair**), version, dernier appel / dernière erreur, utilisateur configurateur |
| `payment_intent` | abstraction paiement (§7-11) : `idempotency_key` unique, `provider_reference`, montant/devise/moyen, `status pending→confirmed/failed/cancelled/expired/amount_mismatch/refunded`, `payment_id` (paiement réel lié à la confirmation), `expires_at`, `provider_response jsonb`, `refunded_amount` |
| `webhook_event` | journal des webhooks entrants (§12-13) : `(connector_id, provider_event_id)` **unique** → déduplication, `signature_valid`, `payload`, `status received/processed/failed/duplicate` |
| `outbound_webhook` + `outbound_webhook_delivery` | webhooks sortants (§32) : abonnements par événement, `secret` de signature, livraisons avec `attempts` / `max_attempts` / back-off `next_attempt_at` / `status pending/delivered/failed/exhausted`, dédup `(webhook, event_id)` |
| permissions | `integrations.{view,configure,payments,webhooks,bank}` + rattachement aux rôles |
| RLS | rattrapage `company_id` sur les 5 tables — **140/151 tables protégées** ; audit sur `integration_connector` et `outbound_webhook` |

### Backend — `Nexora.Application/Integrations` + `Nexora.Infrastructure/Integrations`

```
IIntegrationRegistry        ListAsync / GetAsync (secrets jamais renvoyés),
                            UpsertAsync (par entreprise), SetStatusAsync,
                            TestConnectionAsync (adaptateur simulé §42), RotateSecretAsync (secret rendu 1 fois)
IPaymentGateway             CreateIntentAsync → pending (rejeu idempotent par clé ; connecteur inactif → refus),
                            ProviderCallbackAsync (rappel simulé §42), RefundAsync, ExpireStaleAsync,
                            ConfirmAsync interne : montant fournisseur revérifié (§12) → sinon 'amount_mismatch' sans paiement ;
                            sinon payment RÉEL via IFinanceOperations (operation_uuid = id de l'intention → confirmation idempotente)
IWebhookIngestService       IngestAsync (endpoint public) : résout le connecteur en portée système,
                            vérifie la signature HMAC-SHA256, déduplique, journalise, dispatche vers le paiement.
                            Signature invalide / connecteur inactif → journalisé sous clé synthétique (ne bloque pas le vrai événement)
IPaymentReconciliationService  ReconcileAsync → anomalies : amount_mismatch, missing_payment, stale_pending, duplicate_reference (§15)
IBankReconciliationService  ImportStatementAsync (détection doublons sur (date, montant, référence)),
                            AutoMatchAsync (montant exact + fenêtre ±3 j sur financial_transaction),
                            MatchLineAsync (manuel), ValidateAsync (§16-17)
IOutboundWebhookService     RegisterAsync, EmitAsync (fan-out), DeliverDueAsync, RetryAsync — back-off 1/5/15/60/180 min
IIntegrationCenter          OverviewAsync : connecteurs actifs/en erreur, paiements en attente,
                            webhooks en échec, livraisons dues/épuisées, lignes bancaires non rapprochées, actions (§39-40)
IOutboundWebhookDispatcher  transport HTTP réel ; raccourci bac à sable (§42) pour l'hôte `sim.webhook.local`
```

**Confirmation d'un paiement** : `providerAmount` comparé au montant attendu (arrondi 4 déc.).
Égal → `RecordCustomerPaymentAsync` (paiement réel + écriture équilibrée : débit compte de
règlement, crédit créance affectée ou compte d'attente) puis `payment_intent.status='confirmed'`
+ `payment_id` + événement sortant `payment.confirmed`. Différent → `amount_mismatch`, **aucun
paiement**, visible au rapprochement.

### API — `v1` (`/integrations`)

| Route | Permission |
|---|---|
| `GET connectors` · `GET connectors/{id}` · `GET center` | `integrations.view` |
| `POST connectors` · `POST connectors/{id}/status` · `.../test` · `.../rotate-secret` | `integrations.configure` |
| `GET/POST payments/intents` · `GET .../{id}` · `POST .../{id}/callback` · `.../{id}/refund` · `.../expire` · `GET payments/reconciliation` | `integrations.payments` |
| `POST webhooks/inbound/{connectorCode}` | **anonyme + signature** |
| `GET webhooks/events` · `GET/POST webhooks/outbound` · `.../{id}/status` · `.../emit` · `.../deliveries` · `.../deliveries/{id}/retry` · `.../deliver-due` | `integrations.webhooks` |
| `GET bank/statements` · `POST bank/statements` · `GET .../{id}/lines` · `POST .../{id}/auto-match` · `.../{id}/validate` · `POST bank/lines/{id}/match` | `integrations.bank` |

### Frontend

`frontend/nexora-web/src/integrations/IntegrationsCenterPage.tsx` (route `/integrations`, nav
« Intégrations ») : cartes d'observabilité + actions recommandées, table des connecteurs
(tester / activer-désactiver), intentions de paiement, webhooks entrants (signature + statut),
webhooks sortants + livraisons (rejouer), relevés bancaires (rapprocher). Sections filtrées par
permission. `tsc` + `vite build` OK.

### Démo

`demo_abc_group.sql` : compte financier « prestataire Mobile Money » (`a053`, HTG, lié au GL
5121), connecteur de paiement simulé `moncash-sim` (actif, secret `whsec_demo_pharma_moncash`),
webhook sortant vers `https://sim.webhook.local/ok` abonné à `payment.confirmed` /
`payment.refunded` / `sale.completed`. Rejouable (nettoyage feuilles → racine étendu aux
5 tables d'intégration).

## Vérifications exécutées

**Tests d'intégration `Nexora.IntegrationTests/IntegrationsEcosystemTests` — 11/11** :

| Scénario (§43) | Vérifié |
|---|---|
| Paiement carte confirmé | rappel `confirmed` → intention `confirmed`, **paiement réel** de 500 créé ; 2ᵉ rappel → même `paymentId` (idempotent) |
| Mobile Money confirmé par webhook signé, reçu 2 fois | 1ʳᵉ → `processed`, 2ᵉ → `duplicate` ; **un seul** `payment` (`source_operation_uuid`) |
| Webhook signature invalide | `rejected`, `signature_valid=false`, journalisé sous clé `unverified:…`, intention reste `pending` |
| Webhook montant différent | 900 ≠ 1000 → intention `amount_mismatch`, **0 paiement**, anomalie `amount_mismatch` au rapprochement |
| Paiement timeout | `expires_at` dépassé → `ExpireStaleAsync` → `expired` ; confirmation tardive → `409 integrations.intent_terminal` |
| Remboursement | 150 → `partially_refunded` (refunded=150) ; +250 → `refunded` ; +1 → refusé (> montant) |
| Import relevé avec doublon + rapprochement | 3 lignes dont 1 doublon → `LinesImported=2`, `DuplicatesSkipped=1`, ligne 321 **auto-rapprochée** à la transaction du paiement, ligne 999 `unmatched` |
| Webhook sortant échoue puis réussit | cible `/fail` → livraison `failed` (1 tentative, `next_attempt_at`) ; cible corrigée + `RetryAsync` → `delivered` |
| Connecteur désactivé | `status=inactive` → `CreateIntentAsync` → `integrations.connector_inactive` |
| Rotation d'un secret | ancien secret après rotation → `rejected` ; nouveau secret → `processed` |
| Entreprise A fournisseur 1 / entreprise B fournisseur 2 | connecteurs isolés : la liste de c1 ne voit pas ceux de c2 et inversement (§6) |

**Smoke API (démo)** : `connectors`, `center`, création d'intention → `pending`, webhook signé
`/webhooks/inbound/moncash-sim` → `processed` + intention `confirmed` + `paymentId` lié,
signature invalide → **400**, `payments/reconciliation` → 1 confirmée / 1 liée / 0 anomalie,
`center` → 1 webhook en échec (la tentative non signée) + action recommandée.

**Total backend : 98 tests verts** (16 unitaires + 2 API + 80 intégration), build Debug **et**
Release 0 warning. DB : 31 migrations (V001–V031), **151 tables**, 140 avec RLS. Rebuild +
smoke + démo (rejouée 2×) OK.

## Écarts et décisions

- **Périphériques matériels** (§19-23, §35-36 : imprimantes, scanners, balances, tiroir-caisse,
  afficheur client, terminaux de paiement physiques) : **définis** dans le document de phase ;
  l'abstraction `integration_connector` (type `pos_terminal`) et le principe *device interface →
  driver* sont posés, mais **aucun pilote** n'est livré (dépend du matériel réel). Les scénarios
  §43 correspondants (imprimante indisponible, scanner code inconnu, balance connectée) relèvent
  de l'intégration poste-client.
- **Adaptateur de paiement** : un seul adaptateur **simulé** (§42) est livré — il crée
  l'intention `pending` et attend la confirmation via webhook ou rappel simulé. L'évaluation et
  l'intégration d'un vrai fournisseur (MonCash, cartes, etc.) sont à mener séparément ; le
  moteur reste indépendant du fournisseur choisi (§10, §147).
- **E-commerce / marketplace** (§24-25, §34) : le type de connecteur et le modèle de webhook
  (entrant + idempotence, sortant + retry) sont en place et réutilisables ; la synchronisation
  catalogue/commandes/stock spécifique n'est pas implémentée.
- **Messagerie / Email / SMS / stockage documentaire** (§26-29) : types de connecteurs prévus,
  pas d'envoi réel.
- **Remboursement** : suivi **côté prestataire** (statut + montant + audit + événement sortant
  `payment.refunded`). L'avoir comptable (contre-passation) relève du parcours retour /
  note de crédit de la Phase 6 — séparation assumée.
- **Livraison des webhooks sortants** : `EmitAsync` tente une première remise synchrone puis
  `DeliverDueAsync` / `RetryAsync` rejouent à la demande. La planification automatique du
  back-off (worker) est reportée — les hooks de rejeu sont exposés en API.
- **API publique NEXORA** (§31, §33) : l'API `v1` versionnée + scopes de permission + rate
  limiting existent ; les clés/API credentials tiers, la doc développeur et le sandbox complet
  sont **définis**, non livrés.
- **Import / export** (§30) : `bank_statement` couvre l'import de relevés ; l'import CSV/XLSX
  générique produits/clients/plan comptable n'est pas dans cette phase.

## Couverture des critères de sortie (§44)

| Critère | État |
|---|---|
| Architecture connecteurs définie | ✅ `integration_connector` + contrat commun + isolation par entreprise |
| Abstraction paiements définie | ✅ `payment_intent` → confirmation → paiement réel + rapprochement |
| Mobile Money défini | ✅ intention + référence + expiration + webhook sécurisé (adaptateur simulé) |
| Terminaux définis | ⚙️ abstraction posée, pilotes hors périmètre |
| Banques / rapprochement définis | ✅ import relevés + détection doublons + auto-matching + matching manuel |
| Périphériques définis | ⚙️ *device interface → driver* défini, pilotes hors périmètre |
| E-commerce / messagerie définis | ⚙️ types de connecteurs + modèle webhook réutilisables |
| Import/export défini | ⚙️ relevés bancaires livrés ; import générique défini |
| API publique / webhooks définis | ✅ webhooks entrants (signés, idempotents) **et** sortants (signés, retry) ; API publique définie |
| Sandbox définie | ✅ adaptateur simulé + rappels/webhooks simulés + tests de remboursement et d'erreur |
| Sécurité intégrations définie | ✅ secrets hors config (pointeur coffre), signature HMAC, rotation, isolation par entreprise, révocation (statut), audit |
| Observabilité définie | ✅ centre des intégrations : dernier appel, dernière erreur, en attente, taux d'échec, dernier webhook |
| Gestion des versions définie | ✅ `connector_version` + environnement test/production |
| Scénarios testables | ✅ 11 scénarios §43 automatisés + smoke API |

## Suite — Phase 13

UX/UI, Design System & Expérience Premium : identité visuelle, écrans par rôle, POS tactile,
dashboard, navigation, accessibilité, composants.
