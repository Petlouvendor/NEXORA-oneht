# Phase 34 — Notifications, Communication & Automatisations — implémentation

**Statut : livré et vérifié.** Migration `V047`. 7 tests d'intégration (`NotificationEngineTests`),
5 tests frontend (`notificationsApi.test.ts`). **225 tests backend** verts (18 unit + 7 API +
200 intégration), Debug **et** Release, 0 warning. 45 tests frontend.

Le socle événementiel / automatisation existe déjà et est **validé** par cette phase :

- **Phase 7/31** — `alert_rule` + `IAlertCenter` : alertes évaluées à la volée sur données
  validées (rupture, surstock, expiration, CA en baisse, trésorerie négative, écarts de caisse,
  créances échues — §18-21)
- **Phase 11** (`V016`) — `notification` (centre in-app, RLS `user_id = current_user_id()`),
  `approval_policy` / `approval_request` / `approval_action` : demandeur ≠ approbateur, double
  approbation, seuil configurable (§36)
- **Phase 12** (`V031`) — connecteurs provider-agnostic, `payment_intent`, **webhooks sortants
  signés HMAC + retry + backoff** (`IOutboundWebhookService.EmitAsync` / `DeliverDueAsync` — §28),
  webhooks entrants HMAC idempotents
- **Phase 14** (`V033`) — `workflow` / `workflow_run` + moteur de règles + `IWorkflowService.TestAsync`
  (dry-run sans effet, conditions évaluées + effets prévus — §11-14) ; `automation` / `automation_run`
  (relances / alertes stock / rapports planifiés — §7, §15-18, §41) ; `dunning_policy` (relances
  clients à niveaux J-7 → J+30 — §16) ; `config_version` (versionnement — §13)
- **Phase 16** — `SchedulerRunner` : planificateur de fond **idempotent** par entreprise (§7, §29, §32)
- **Phase 13** (`V032`) — `user_preference` (`notification_prefs` jsonb libre pour l'UX)

La Phase 34 ajoute la **couche de distribution** qui manquait : les automatisations écrivaient
déjà `nexora.notification` par INSERT direct, **sans préférences, sans routage multi-canal, sans
gabarit, sans file de remise fiable**. Cette phase les fait passer par un **moteur de notification**
(§2-5, §22-23, §28-31, §45).

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-2 | Transformer un événement en action fiable ; identité + version + propriétaire ; actions critiques sous approbation ou interdites ; idempotence ; erreurs visibles & récupérables | `INotificationService.NotifyAsync` : déduplication → préférences → rendu de gabarit → notification interne + mise en file. `automation` / `workflow` portent code + version + `created_by` (Phase 14). Les actions financières (§10) restent sous `IApprovalWorkflowService` / permissions (Phases 11/27-29) — le moteur ne les exécute jamais |
| 3 | Canaux : interne, email, SMS, WhatsApp, push web/mobile, webhook, tâche, centre — **isolés derrière une couche remplaçable** | **`INotificationChannel`** (`Channel`, `SendAsync → ChannelSendResult`). Implémentations par défaut : `LoggingEmailChannel` / `LoggingSmsChannel` / `LoggingWebhookChannel` (journalisent, retournent `Ok`/`Retry`). Un fournisseur réel (SMTP, Twilio, Meta) se substitue sans toucher au moteur. WhatsApp / push / tâche : **backlog** (l'abstraction est prête) |
| 4 | Centre : non lue / lue / **épinglée** / **archivée** / priorité / catégorie / entreprise / lien / date / **expiration** | `notification` += `category` / `priority` (1–5) / `pinned_at` / `archived_at` / `expires_at` / `dedup_key` / `source`. `INotificationService.ListAsync(NotificationQuery{state,category})`, `UnreadCountAsync`, `MarkReadAsync`, `SetPinnedAsync`, `SetArchivedAsync`. `GET /api/v1/notifications` + `PATCH /{id}/read` + `POST /{id}/{pin,archive,…}` |
| 5 | Préférences : **canal par type**, instantané / résumé quotidien-hebdo / silencieux, **heures autorisées**, langue, entreprise, **priorité minimale** | **`notification_preference`** (`user_id`, `company_id` NULL = toutes, `category` NULL = toutes, `in_app`/`email`/`sms`/`webhook`, `min_priority`, `digest` instant/daily/weekly/off, `quiet_from`/`quiet_to`). `INotificationPreferenceService` + `GET/POST/DELETE /api/v1/notification-preferences`. `NotifyAsync` résout la préférence **la plus spécifique** (entreprise+catégorie > entreprise > catégorie > défaut) ; `priority > min_priority` → ignoré ; heures calmes → la remise externe est différée à `quiet_to`. **`digest ≠ instant` → la remise externe est retenue** (le batch de résumé est un backlog) |
| 6 | Événements déclencheurs (vente, paiement, stock faible, expiration, facture échue, écart caisse, alerte sécurité, période à clôturer…) | `NotifyRequest.Category` + `Source`. Producteurs câblés : les 3 automatisations `dunning` / `low_stock_alert` / `expiry_alert` (Phase 14) passent désormais par `NotifyAsync`. Les autres producteurs (`login.*` / `mfa.*` de la Phase 33, ventes, clôture) sont **prêts à brancher** sur le même point d'entrée |
| 7 | Déclencheurs temporels (date, avant/après échéance, quotidien/hebdo/mensuel, intervalle, calendrier ouvré) | `automation.schedule` jsonb + `ComputeNextRun` + `SchedulerRunner` (Phase 14/16) — inchangés. `mfa_challenge` / suspenses / lots utilisent le même planificateur |
| 8 | Conditions (montant, statut, entreprise, rôle, catégorie, client/fournisseur/produit, date, score, AND/OR) | `workflow.definition` jsonb (`{logic, conditions[]}`) + `IWorkflowService.EvaluateAsync` (Phase 14) ; les automatisations appliquent leurs propres filtres SQL (retard, point de commande, péremption) |
| 9 | Actions (notification, email, SMS, tâche, rappel, étiquette, changement de statut, demande d'approbation, brouillon, webhook, file, alerte) | `NotifyAsync` couvre notification / email / SMS / webhook. Demande d'approbation : `IApprovalWorkflowService` (Phase 11). Brouillon de document : `IDocumentService` (Phase 30). Tâche / rappel / étiquette : **backlog** (table `task` à venir) |
| 10 | Actions financières sensibles **jamais exécutées librement** par une règle | Aucun exécuteur d'automatisation ne crée de paiement / n'ouvre de période / ne radie de créance. `automation.kind` est une liste fermée (`dunning`, `low_stock_alert`, `expiry_alert`, `scheduled_report`, …) sans effet financier |
| 11-14 | Moteur de workflow (étapes / conditions / approbation / actions) ; workflows configurables & **versionnés** (draft/test/publié/suspendu/archivé, version précédente conservée, rollback) ; **mode simulation** (données fictives, prévisualiser conditions & actions, tester destinataires & template, aucune modif prod) | `IWorkflowService` + `workflow.status` (draft/active/inactive) + `.TestAsync` (Phase 14). Rendu de gabarit testable : **`IMessageTemplateService.RenderAsync`** (aperçu §22, teste les variables manquantes). `message_template.version` s'incrémente à chaque upsert. Cycle test complet draft→test→publié : partiellement backlog |
| 15-17 | Tâches automatiques ; relances clients (modèle & canal configurables, arrêt après paiement/litige, escalade) ; relances fournisseurs | `automation` kind `dunning` → `RunDunningAsync` : lit `dunning_policy.levels`, choisit le niveau selon `days_late`, **`NotifyAsync(dunning.reminder, DedupKey = "dunning:{receivableId}:L{level}")`**. `dunning_opt_out` + statut de créance filtrent (arrêt). Relances fournisseurs (`supplier_reminder`) : exécuteur défini, **backlog** |
| 18-21 | Alertes stock / finance / comptables / sécurité | Stock : `low_stock_alert` / `expiry_alert` → `NotifyAsync(stock.low / stock.expiring)`. Finance / compta / sécurité : `IAlertCenter` (Phase 7/31) + évènements Phase 33 — **prêts à router via `NotifyAsync`** |
| 22-23 | **Templates** (email/SMS/WhatsApp/push/interne, variables dynamiques, langues, version, aperçu, tests) ; **variables autorisées uniquement, valeurs absentes gérées, aucune injection** | **`message_template`** (`company_id` NULL = système, `key` + `channel` + `locale`, `subject` + `body`, `variables` jsonb liste blanche, `version`, `status`). **`TemplateRenderer`** : substitue **uniquement** les jetons `{{clef}}` présents dans le contexte fourni ; variable absente → chaîne vide + signalée ; **aucune évaluation, aucune interpolation récursive** — un contenu injecté n'est jamais ré-interprété. `IMessageTemplateService.UpsertAsync` (crée un gabarit d'entreprise, `version + 1` à chaque édition), `RenderAsync`. `GET/POST /api/v1/message-templates` + `/render` (`notifications.manage_templates`). **5 gabarits système seedés** (`dunning.reminder` in_app+email, `stock.low`, `stock.expiring`, `notification.delivery_failed`) |
| 24-27 | Email / SMS / WhatsApp / Push — expéditeur, statut de livraison, échec, historique | `notification_outbox` porte `channel` / `subject` / `body` / `address` / `status` / `attempts` / `last_error` / `sent_at` — l'historique de remise. Les spécificités fournisseur (nom affiché, segmentation SMS, template approuvé WhatsApp, deep link push) : **backlog**, l'implémentation de canal les portera |
| 28 | **Webhooks** (URL, secret/signature, timeout, retry, backoff, idempotency, statut, journal) | `IOutboundWebhookService` (Phase 12) reste la voie des webhooks *abonnés à un événement*. `LoggingWebhookChannel` couvre le webhook *comme canal de notification*. Signature HMAC : Phase 12 |
| 29-31 | **Fiabilité** : queue asynchrone, retry, **backoff exponentiel**, **dead-letter queue**, idempotence, correlation ID, rejeu contrôlé. **Déduplication** : event/rule/execution ID, idempotency key, fenêtre anti-duplication. **Gestion d'erreurs** : temporaire / définitive, retry, escalade, **notification administrateur**, dead letter, résolution manuelle, historique | **`notification_outbox`** : `status` pending/sending/sent/failed/**dead**, `attempts`/`max_attempts` (défaut 5), `next_attempt_at`, `correlation_id`, `dedup_key`. **`NotificationDispatcher.DispatchDueAsync`** (piloté par `SchedulerRunner`) : verrou optimiste (`status='sending'`), tente le canal, `Sent` → `sent` ; `Transient && attempts < max` → `failed` + `next_attempt_at = now + 2^attempts min` (plafond 60) ; sinon → **`dead`** + **`NotifyAsync(notification.delivery_failed)` vers les admins de l'entreprise** (jamais de cascade : un échec de catégorie `delivery_failed` n'escalade pas). Déduplication : pré-contrôle `(user_id, dedup_key)` sur `notification` (index unique partiel) + contrainte `(dedup_key, channel)` sur l'outbox |
| 32-33 | Ordonnancement (file prioritaire, immédiat / planifié / lourd, limite de concurrence, fenêtres) ; **quotas** (emails/SMS/webhooks/workflows par entreprise & utilisateur, alerte, blocage) | Le distributeur traite par lots de 50, `ORDER BY priority, next_attempt_at`. Quotas par entreprise / utilisateur : **backlog** (table `notification_quota`) |
| 34-35 | **Sécurité** : chaque règle a un scope, actions sous **identité d'exécution**, **secrets non accessibles aux templates**, permissions vérifiées, logs protégés, webhooks signés. **Identité d'automatisation** : compte technique, permissions minimales, scope limité, aucune permission implicite d'admin | `NotifyAsync` s'exécute dans le périmètre de l'entreprise active (`tenant.ActiveCompanyId`) → jamais de fuite vers une autre entreprise (§37). Le distributeur agit en **portée système** (`BeginSystemScope`) pour l'entreprise concernée. `TemplateRenderer` ne voit **que** le dictionnaire de variables fourni — aucun accès aux secrets, à la base, au contexte. `audit_row` sur `message_template` / `notification_preference` / `notification_outbox` |
| 36 | Approbation d'une automatisation | `IApprovalWorkflowService` (Phase 11) — une automatisation crée une `approval_request` ; l'action suit l'approbation |
| 37 | **Multi-entreprises** : règle propre / groupe, destinataires filtrés, **aucune notification vers une autre entreprise par erreur**, templates propres ou groupe | `notification.company_id` + `notification_outbox.company_id` = entreprise active à l'émission. `notification_preference.company_id` filtre par entreprise. `message_template` : système (NULL) ou entreprise (RLS `company_id IS NULL OR rls_allows_company`). Test `Marking_read_and_the_per_user_scope` : `ListAsync` filtre `WHERE user_id = courant` |
| 38-39 | Local / cloud / hybride ; **offline** (queue, synchro, déduplication, actions critiques bloquées) | La file `notification_outbox` fonctionne à l'identique quel que soit le mode ; la synchro offline (Phase 10) rejoue des opérations idempotentes. Cache de permissions offline : Phase 33 |
| 40 | Planification de rapports (quotidien/hebdo/mensuel, destinataires, format, filtres, heure, fuseau, historique) | `automation` kind `scheduled_report` + `scheduled_export` (Phase 14) — exécuteur défini, envoi via `NotifyAsync` : **backlog partiel** |
| 41-42 | Automatisations métier & exemples (réappro suggéré, relance client, alerte expiration/budget, tâche de clôture, rappel inventaire/paiement fournisseur, alerte rapprochement) | `dunning` / `low_stock_alert` / `expiry_alert` livrés. Réappro / budget / clôture / rapprochement : exécuteurs à écrire sur le même modèle |
| 43-44 | **Audit** (création/modification/publication/suspension/exécution/action/échec/retry/approbation/rollback/destinataire/canal) ; **journal d'exécution** (execution/rule/event ID, dates, statut, étape, résultat, erreur, retry count, approbateur) | `automation_run` / `workflow_run` (Phase 14) : `mode`, `status`, `result` jsonb, `error`, `attempt`, `started_at`/`finished_at`. `notification_outbox` : `correlation_id`, `attempts`, `last_error`, `sent_at`. Triggers `audit_row` sur les 3 nouvelles tables |
| 45 | API principales | **`GET /api/v1/notifications`** (+ `unread-count`, `PATCH /{id}/read`+`/unread`, `POST /read-all`, `POST /{id}/{pin,unpin,archive,unarchive}`), **`GET/POST/DELETE /api/v1/notification-preferences`**, **`GET/POST /api/v1/message-templates`** + **`POST /message-templates/render`**. `automation-rules` / `automation-executions` / `webhooks` : servis par `/admin/automations*` & `/integrations/webhooks*` (Phases 14/12) — **écart de nommage assumé** |
| 46 | Événements (`NotificationCreated`, `NotificationSent`, `NotificationFailed`, `AutomationExecuted`, `WebhookDelivered`…) | Journalisés via `notification` / `notification_outbox` / `automation_run` + `audit_log`. Bus d'évènements dédié : **backlog commun aux phases 16-34** |
| 47 | Tests | `NotificationEngineTests` (7) — cf. ci-dessous |
| 48 | Scénario complet (facture → J-7 rappel → échéance → J+7 relance SMS → J+30 escalade → recouvrement) | Couvert par la chaîne `automation` `dunning` → `dunning_policy.levels` → `NotifyAsync(dunning.reminder)` → `notification_outbox` (email/SMS) → `NotificationDispatcher`. L'escalade responsable (`level ≥ 3` → priorité 2) est en place ; le passage automatique au module recouvrement reste manuel (Phase 27) |
| 49 | Critères de sortie | Centre + préférences + gabarits + variables + retry + idempotence + dead-letter + identité d'exécution + multi-entreprises + audit + API : **livrés**. Email/SMS/WhatsApp/push réels, quotas, workflow d'approbation d'automatisation, rapports planifiés, tâches/rappels : **backlog explicite** |

## `V047` — contenu

- **`notification`** += `category` / `priority` (`CHECK 1..5`) / `pinned_at` / `archived_at` /
  `expires_at` / `dedup_key` / `source` (`CHECK system|automation|workflow|alert|security|manual`).
  Index unique partiel `notification_dedup_uk (user_id, dedup_key) WHERE dedup_key IS NOT NULL`.
- **`notification_preference`** — RLS `user_id = current_user_id()` (comme `notification`).
  `UNIQUE NULLS NOT DISTINCT (user_id, company_id, category)`.
- **`message_template`** — RLS custom (système `company_id IS NULL` visible de tous).
  `UNIQUE NULLS NOT DISTINCT (company_id, key, channel, locale)`. 5 gabarits système seedés.
- **`notification_outbox`** — `enable_company_rls`. `UNIQUE NULLS NOT DISTINCT (dedup_key, channel)`,
  index `(next_attempt_at) WHERE status IN ('pending','failed')`.
- Triggers `audit_row` sur les 3 nouvelles tables. Permission `notifications.manage_templates`
  (super_administrator / group_administrator / owner_direction).
- **182 tables (+3), 170 en RLS (+3)**, 47 migrations (V001–V047).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Notifications/NotificationContracts.cs` |
| Application | `Nexora.Application/Notifications/INotifications.cs` — `INotificationService`, `INotificationPreferenceService`, `IMessageTemplateService`, `INotificationDispatcher`, `INotificationChannel` (+ `ChannelSendResult`) |
| Infrastructure | `Nexora.Infrastructure/Notifications/` — `TemplateRenderer` (regex `{{…}}`, source-generated), `NotificationServices.cs` (`SqlMessageTemplateService`, `SqlNotificationPreferenceService`), `SqlNotificationService.cs` (cœur), `NotificationDispatcher.cs` (`NotificationDispatcher` + `LoggingEmail/Sms/WebhookChannel`). Câblage : `SchedulerRunner` += `INotificationDispatcher.DispatchDueAsync` ; `SqlAutomationService` injecte `INotificationService` (3 INSERT bruts → `NotifyAsync`). DI dans `Infrastructure/DependencyInjection.cs` |
| API | `Controllers/NotificationsController.cs` (+ `NotificationPreferencesController`), `Controllers/MessageTemplatesController.cs` ; `PermissionCatalog.Notifications.ManageTemplates` |

### Points de conception

- **Pas de `INSERT … RETURNING` via `SqlQuery`** : EF le refuse (« non-composable SQL »). Les
  services génèrent l'`id` en C# et insèrent via `ExecuteSqlAsync`, ou upsert puis `SELECT` la clé.
- **Résolution de préférence** : une seule requête, `ORDER BY (company_id IS NOT NULL) DESC,
  (category IS NOT NULL) DESC LIMIT 1` — le plus spécifique gagne, défaut sinon (`in-app` seul,
  toutes priorités).
- **Anti-cascade** (§31) : `NotificationDispatcher.NotifyAdminOfFailureAsync` ne s'exécute pas
  pour une ligne dont la catégorie est déjà `delivery_failed`.
- **Backoff** : `2^attempts` minutes plafonné à 60.

## Tests d'intégration — `NotificationEngineTests` (7, `[Collection("finance")]`)

| Test | § | Vérifie |
|---|---|---|
| `Notify_creates_an_in_app_notification` | 2, 4 | `NotifyAsync` → 1 ligne `notification`, titre & corps rendus, `UnreadCountAsync = 1` |
| `Channel_preferences_decide_which_outbox_rows_are_queued` | 5 | défaut → 0 outbox ; `email = true` sur la catégorie → 1 ligne `notification_outbox` canal `email` |
| `Below_the_minimum_priority_the_notification_is_dropped` | 5 | `min_priority = 2`, notif priorité 4 → `NotificationsCreated = 0`, `Skipped = 1` |
| `The_dedup_key_prevents_a_duplicate_notification` | 30 | même `DedupKey` deux fois → 2ᵉ `Skipped`, 1 seule ligne en base |
| `Template_rendering_is_whitelisted_and_flags_missing_variables` | 23 | `{{customer.name}}` substitué ; `{{invoice.balance}}` non fourni → signalé ; valeur injectée `{{invoice.balance}}` **non ré-interprétée** ; jeton inconnu → vide |
| `The_dispatcher_dead_letters_after_the_maximum_attempts` | 28-31 | canal `webhook` sans adresse → `failed` (1) puis `dead` (2), `attempts = 2` |
| `Marking_read_and_the_per_user_scope` | 4, 37 | `MarkReadAsync` → `UnreadCount` retombe ; Bob ne voit pas la notification d'Alice |

## Frontend

`src/notifications/` — `notificationsApi.ts` + **`NotificationsPage.tsx`** (route `/notifications`,
nav « Notifications ») : onglets **Non lues / Toutes / Épinglées / Archivées** (marquer lu,
épingler, archiver, tout marquer lu) + onglet **Préférences** (table des règles de routage +
formulaire : catégorie, canaux, priorité mini, résumé). **Cloche du bandeau** repointée sur
`/notifications` avec **badge du nombre de non-lues** (poll 60 s via `GET /notifications/unread-count`).

Vitest `notificationsApi.test.ts` (5) → **45 tests frontend**.

## Smoke API (port 5080, `direction@abc-group.demo`)

- `GET /notifications` → `{ items:[], total:0, unread:0 }` ; `GET /message-templates` → 5 gabarits
  système ; `POST /message-templates/render {key:"dunning.reminder", channel:"email", variables:{…}}`
  → sujet + corps rendus, variables manquantes listées.
- `POST /notification-preferences {category:"dunning", email:true}` → préférence créée ;
  `GET /notification-preferences` la renvoie.
- Automatisation `dunning` exécutée (`POST /admin/automations/{id}/run`) → notification `dunning`
  visible dans `GET /notifications`, ligne `notification_outbox` email en file.
- Caissier sur `GET /message-templates` → **403** (`notifications.manage_templates` absente).

## Manques assumés (backlog)

- Canaux réels : SMTP, Twilio SMS, Meta WhatsApp (template approuvé), WebPush (l'abstraction
  `INotificationChannel` est prête) (§24-27)
- Batch de résumé quotidien / hebdomadaire (`digest` est stocké et respecté — la remise externe
  est *retenue*, mais aucun job ne regroupe encore) (§5)
- Quotas par entreprise / utilisateur + alerte + blocage (`notification_quota`) (§33)
- Table `task` / `reminder` + actions « créer tâche / rappel / étiquette » (§9, §15)
- Workflow d'approbation d'une automatisation de bout en bout (§36) ; cycle draft→test→publié→rollback
  complet pour les gabarits (§13-14)
- Rapports planifiés avec destinataires & format (`scheduled_report` a un exécuteur stub) (§40)
- Bus d'évènements `NotificationSent` / `AutomationExecuted` (§46) — backlog commun aux phases 16-34
- Fuseau horaire des heures calmes : aujourd'hui évaluées en UTC (§5)
