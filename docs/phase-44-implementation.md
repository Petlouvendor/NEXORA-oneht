# Phase 44 — Mobile, terrain & multi-appareils — implémentation

**Statut : livré et vérifié.** Migration `V057` (5 tables neuves, 3 colonnes ajoutées sur
`sync_device`, 2 permissions). 6 tests d'intégration (`MobileFieldOpsTests`), 3 tests API
(`MobileEndpointsTests`), 4 tests frontend (`mobileApi.test.ts`).
**331 tests backend** verts (18 unit + 35 API + 278 intégration), Debug **et** Release, 0 warning,
vérifiés après un rebuild complet de la base (`V001`→`V057`, 235 tables). 85 tests frontend.
`tsc` + `vite build` verts.

Le socle mobile/offline existait déjà et reste **validé** : `sync_device`/`sync_operation`/
`sync_conflict`/`sync_batch`/`offline_policy` (Phase 10, V017/V029) — file idempotente par
`operation_uuid`, conflits **persistés** et résolubles, jamais un écrasement silencieux (§29).
`ISyncService.ProcessAsync` ne rejouait jusqu'ici QUE les ventes (`entity_type = 'sale'`) : cette
phase **étend le même moteur**, sans le dupliquer, aux opérations terrain (livraison,
intervention) — l'idée centrale du cahier des charges (§2 : mêmes règles, mêmes permissions, sync,
données centralisées) appliquée littéralement à la synchronisation elle-même.

### Socle réutilisé

- **Phase 10** — `sync_device`/`IDeviceRegistryService`/`IOfflinePolicyService`/`ISyncService`
  (moteur offline complet, non reconstruit — seulement étendu)
- **Phase 25** — `OrderService.CompleteOrderAsync` → réutilisé par la complétion d'une étape de
  livraison liée à une commande (statut de commande inclus, §52)
- **Phase 34** — `notification_outbox`/`message_template` autorisaient déjà la valeur `'push'`
  depuis le V047 (jamais implémentée) ; `INotificationService` étendu d'une méthode `SendPushAsync`
- **Phase 43** — `ITaskService` (tâches, réexposées dans le bootstrap mobile)
- **Phase 38** — `support_ticket` (support NEXORA↔abonné) confirmé **sans rapport** avec les
  interventions terrain d'une entreprise cliente — pas de fusion

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 1-2 | Mêmes règles/permissions/identité sur tous les appareils ; offline maîtrisé ; aucune synchronisation ne contourne les contrôles serveur | Architecture déjà conforme (Phase 3 JWT+RLS, Phase 10 offline) — non redistribuée. Cette phase l'illustre en étendant le moteur offline existant plutôt qu'en créant une file séparée pour le mobile |
| 3-4 | Cibles appareils ; approche applicative (web responsive/PWA/POS tactile) | **Hors périmètre applicatif** — web responsive (Phase 13) et POS tactile (Phase 6) déjà livrés ; PWA/app native = décision de packaging frontend, pas un module serveur, **backlog explicite** |
| 5-10 | Profils mobiles ; dashboard mobile ; dirigeant/manager/comptable mobile | **Backlog explicite pour dirigeant/manager/comptable** — leurs tableaux de bord existent déjà (Phase 7) et sont accessibles depuis n'importe quel appareil via le même frontend responsive ; aucune vue mobile *distincte* n'est construite cette phase (l'app n'a pas de client natif séparé) |
| 10 | Commercial mobile (clients/commandes/catalogue/prix/stock/devis/relances) | **Déjà couvert** par les modules existants (Phase 6/23/25/27/30) accessibles depuis le même frontend — aucune redirection nécessaire |
| 11-12 | Agent inventaire ; **livreur** (tournées, adresse, statut, preuve livraison, signature, photo, paiement à la livraison, retour) | Agent inventaire — **déjà couvert** (Phase 24, scan/lot/expiration/écart). **Livreur — nouveau** : `delivery_route`/`delivery_stop` (statuts `pending→en_route→arrived→delivered/failed/returned`), preuve de livraison = pièce jointe photo/signature (§22-23), paiement à la livraison encaissé via `OrderService.CompleteOrderAsync` (§52) |
| 13 | Technicien (interventions, client, équipement, checklist, pièces, temps, photo, signature, statut, rapport) | **`field_intervention`** — comble un manque réel (aucune fiche d'intervention n'existait ; `support_ticket` Phase 38 est un concept différent). Checklist/pièces en jsonb, temps passé, signature (pièce jointe), rapport texte, cycle `scheduled→in_progress→completed/cancelled` |
| 14-17 | POS mobile ; tablette restaurant/pharmacie/commerce | **Déjà livrés** (Phase 6 POS tactile + offline, Phase 8 restaurant, Phase 24 lots pharmacie, Phase 5/24 commerce) — non redistribués, le même frontend responsive sert ces contextes |
| 18-19 | Appareil partagé (PIN, verrouillage, changement utilisateur, audit) ; biométrie système | **Backlog explicite** — PIN/verrouillage/biométrie sont des politiques d'appareil/OS, pas des données serveur ; NEXORA ne stocke jamais de biométrie brute (conforme par construction, rien à implémenter côté serveur) |
| 20 | Notifications mobiles (push, centre, priorité, deep link, silence, résumé) | **Canal push implémenté** — `notification_outbox`/`message_template` autorisaient déjà `'push'` (V047) sans jamais avoir de canal ; `LoggingPushChannel` (stub, comme email/sms/webhook) + **`INotificationService.SendPushAsync`** (envoi direct vers les appareils mobiles actifs d'un utilisateur, hors routage par préférence pour ne pas rouvrir les écrans de préférences Phase 34) ; utilisé par la création de tournée (notifie le livreur). Centre de notifications/deep link — **déjà livrés** (Phase 34), réexposés sous `/mobile/notifications`. Résumé/regroupement — **backlog** |
| 21 | Approbations mobiles | **Déjà livré** (Phase 11 `IApprovalWorkflowService`), accessible depuis n'importe quel appareil — non redistribué |
| 22-23 | Signature (client/livraison/intervention, horodatage, identité, document associé, audit) ; photo/caméra (code-barres/QR/document/preuve/inventaire/équipement) | **`attachment`** (NEW) — primitive minimale photo/signature (blob + hash SHA-256, capturé par + horodaté, `reference_type`/`reference_id` polymorphe comme `notification`/`task`). Volontairement simple : pas de versioning ni de recherche — **Phase 45 (GED) généralisera** si un vrai stockage de documents devient nécessaire. Scanner code-barres/QR — déjà couvert côté frontend (Phase 6 POS caméra) |
| 24 | GPS (position ponctuelle, suivi livraison, jamais permanent sans politique, historique limité) | **Premier usage de coordonnées géographiques dans tout le schéma** — `delivery_stop.latitude/longitude`, capturées **uniquement au moment de la preuve de livraison** (`CompleteStopAsync`), jamais un suivi continu — conforme à « pas de suivi permanent sans politique » par construction (aucune table de trace GPS continue n'existe) |
| 25-27 | Mode offline (cache/queue/chiffrement/expiration) ; opérations autorisées/interdites hors ligne | **Déjà livré** (Phase 10 `offline_policy`) — étendu : les opérations terrain (livraison, intervention) rejouent désormais le **même moteur** de queue/idempotence que la vente, jamais une file parallèle. Chiffrement local du cache — responsabilité du client, hors périmètre serveur |
| 28-29 | Synchronisation (queue→auth→validation→idempotence→conflit→confirmation) ; conflits (version/timestamp/priorité/résolution manuelle/journal, aucun écrasement silencieux) | **Déjà livré** (Phase 10) — étendu : `ProcessAsync` dispatch désormais `sale`, `delivery_stop`, `field_intervention` ; un conflit métier (étape déjà livrée, intervention déjà close) crée un `sync_conflict` de type `business`, résoluble comme les conflits stock/crédit existants — **aucune duplication du mécanisme** |
| 30-31 | Données sensibles offline (minimisation/chiffrement/expiration/effacement) ; sécurité appareil (PIN/verrouillage/session/révocation/liste/compromission) | Révocation/liste d'appareils — **déjà livrés** (Phase 10, `IDeviceRegistryService`). Chiffrement du stockage local, détection de compromission — responsabilité client/OS, **hors périmètre serveur** |
| 32 | Gestion appareils (nom/type/OS/version app/dernière activité/dernière sync/entreprise/caisse/statut/révocation) | **Déjà livré** (Phase 10, `sync_device`) + **étendu** : `user_id` (un mobile est **personnel**, contrairement à un poste POS **partagé** — `RegisterAsync` lie automatiquement `device_type='mobile'` à l'utilisateur appelant), `push_token`/`push_provider`. Alias canoniques §48 : `GET/POST ~/api/v1/devices`, `POST ~/api/v1/devices/{id}/revoke` (même registre que `/sync/devices`, pas une deuxième table) |
| 33 | MDM / entreprise (politiques, effacement à distance, apps autorisées, certificats) | **Hors périmètre serveur** — relève d'une solution MDM tierce (infrastructure), comme documenté pour les sujets équivalents des phases précédentes (CI/CD, WAL, etc.) |
| 34-35 | Impression mobile (Bluetooth/Wi-Fi/réseau/ticket/A4/réimpression/état) ; périphériques (scanner/balance/lecteur carte/tiroir/écran client) | **Hors périmètre serveur** — pilotage matériel côté client (déjà partiellement couvert côté frontend POS, Phase 6/13) ; rien à ajouter côté API |
| 36 | Synchronisation documents (upload différé/compression/reprise/hash/statut/suppression cache) | `attachment.sha256_hash` couvre l'intégrité ; upload différé/reprise = comportement client (queue offline existante, §25-29) ; compression — **backlog** |
| 37-39 | Performance mobile ; réseaux faibles ; batterie | **Hors périmètre serveur** — pagination/limites déjà en place sur les listes (héritage Phase 7) ; le reste est un choix client (cache, lazy loading, fréquence de sync) |
| 40 | Multi-entreprises mobile (sélecteur, entreprise active visible, permissions par entreprise, aucune donnée interentreprise non autorisée en cache) | **Déjà garanti structurellement** — `IMobileBootstrapService.PullAsync` filtre strictement par `ITenantContext.ActiveCompanyId` (RLS + filtre explicite, comme tous les services depuis la Phase 40) ; aucune extraction cross-entreprise n'existe dans le bootstrap |
| 41-42 | Deep links ; partage (lien temporaire, permissions, expiration, audit) | Deep link — `action_url` existe déjà sur `notification` (Phase 34), utilisé par la notification de tournée (`/field-ops`). Partage de document via l'OS — **backlog**, lié à Phase 45 (GED) |
| 43-44 | Accessibilité mobile ; internationalisation | **Déjà livrées** au niveau du design system (Phase 13/40 : contraste élevé, échelle de texte, `ht-HT`) — le même frontend responsive en hérite automatiquement, rien de mobile-spécifique à ajouter |
| 45 | Mise à jour (version, compatibilité backend, migration cache, force update, rollback) | `mobile_config.minAppVersion` — **valeur statique** pour l'instant (`"1.0.0"`), pas encore branchée sur un mécanisme de version réelle côté client ; **backlog** (aucun client mobile natif n'existe encore pour l'exercer) |
| 46 | Crash/diagnostics minimisés (version/OS/appareil/étape/correlation ID, aucune donnée sensible inutile, opt-in) | **`crash_report`** (NEW) — journal d'ajout seul (comme `assistant_query`, Phase 43), `correlation_id`, `app_version`/`os`/`os_version`, message + extrait de pile ; aucun champ de donnée métier/sensible |
| 47 | Mobile analytics (écran/performance/erreurs/usage/adoption/offline/sync/batterie, minimisés et transparents) | **Backlog explicite** — nécessite une télémétrie client dédiée, hors périmètre de cette phase serveur |
| 48 | API principales | Toutes servies — voir § Backend/API. `/mobile/sync`, `/mobile/sync/push`, `/mobile/conflicts`, `/mobile/conflicts/{id}/resolve` sont des **alias purs** de `ISyncService` (Phase 10), zéro logique dupliquée |
| 49 | Événements | Portés par les colonnes de statut (`delivery_stop.status`, `field_intervention.status`, `sync_operation.status`) + `audit_log` (trigger) + `notification`/`notification_outbox` (push) — cohérent avec le style déjà en place (pas de bus d'évènements séparé) |
| 50 | Tests | `MobileFieldOpsTests` (6) + `MobileEndpointsTests` (3) — cf. ci-dessous |
| 51-53 | Scénarios terrain / livraison / inventaire | Scénario livraison directement reproduit par `Delivery_route_creation_notifies_driver_and_stop_without_order_completes_with_proof` (tournée → notification push → preuve → statut). Scénario terrain (réseau faible → brouillon local → sync) reproduit par `Delivery_stop_replays_through_offline_sync_engine_…` via le moteur offline. Scénario inventaire — **déjà couvert** par le cycle de lots (Phase 24), non redistribué |
| 54 | Critères de sortie | Livreur/technicien/pièces jointes/GPS/canal push/gestion appareils étendue/moteur offline étendu/API/événements/tests **livrés** ; profils mobiles dédiés dirigeant/manager/comptable/commercial, PWA/app native, MDM, impression/périphériques mobiles, mobile analytics, mise à jour versionnée réelle : **backlog explicite**, détaillé ci-dessus |
| 55 | Étape suivante — Phase 45 (GED, documents & signature) | hors périmètre — `attachment` (cette phase) est délibérément minimal pour que Phase 45 le généralise sans reprendre à zéro |

## `V057` — contenu (5 tables neuves, 3 colonnes, 2 permissions, 1 méthode d'interface)

- **`attachment`** (NEW, RLS entreprise) — blob (`bytea`) + `sha256_hash`, `kind` photo|signature,
  `reference_type`/`reference_id` polymorphe, ≤ 5 Mo (`CHECK`). Minimal par choix — Phase 45 (GED)
  généralisera si besoin (versioning/recherche/OCR restent hors périmètre ici).
- **`delivery_route`**/**`delivery_stop`** (NEW, RLS entreprise) — première utilisation de
  coordonnées géographiques (`latitude`/`longitude numeric(9,6)`) dans tout le schéma NEXORA ;
  `delivery_stop.sales_order_id` nullable (composite FK vers `sales_order`) ; complétion d'une
  étape liée à une commande = `OrderService.CompleteOrderAsync` (§52).
- **`field_intervention`** (NEW, RLS entreprise) — `checklist`/`parts_used` jsonb, cycle
  `scheduled→in_progress→completed/cancelled`.
- **`crash_report`** (NEW, RLS entreprise) — journal d'ajout seul, diagnostics minimisés (§46).
- **`sync_device`** (Phase 10, V017, étendu) — `+ user_id` (personnel pour `device_type='mobile'`,
  NULL pour un poste partagé), `+ push_token`/`push_provider` (`CHECK` fcm|apns|webpush) — la
  colonne `notification_outbox.channel`/`message_template.channel` autorisait déjà `'push'` depuis
  le V047, jamais implémentée.
- `trg_audit_*` sur `delivery_route`/`delivery_stop`/`field_intervention` (pas sur `attachment`
  ni `crash_report`, journaux d'ajout seuls). Permissions **`field_ops.{view,manage}`** — aucun
  rôle « livreur »/« technicien » dédié n'existe encore (constat, pas construit cette phase) :
  accordées à `manager`/`salesperson`/`stock_manager` (lecture pour `cashier`).
- **235 tables (+5), 200 RLS (+5)**, 57 migrations (V001–V057).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Mobile/MobileContracts.cs` |
| Application | `Nexora.Application/Mobile/IMobile.cs` — `IAttachmentService`, `IDeliveryService`, `IFieldInterventionService`, `ICrashReportService`, `IMobileBootstrapService` ; `Nexora.Application.Sync.IDeviceRegistryService += RegisterPushTokenAsync` (Phase 10, étendue) ; `Nexora.Application.Notifications.INotificationService += SendPushAsync` (Phase 34, étendue) ; `PermissionCatalog.FieldOps` |
| Infrastructure | `Mobile/{AttachmentService,DeliveryService,FieldInterventionService,MobileBootstrapAndCrashServices}.cs` ; `Notifications/NotificationDispatcher.cs += LoggingPushChannel` ; `Sync/SyncServices.cs` étendu (`ProcessDeliveryStopAsync`/`ProcessFieldInterventionAsync`) ; `Sync/DeviceAndPolicyServices.cs` étendu (`user_id` à l'enregistrement, `RegisterPushTokenAsync`) |
| API | `Nexora.Api/Controllers/MobileController.cs` (`/api/v1/{mobile/*,attachments,field-ops/*}`) + alias sur `SyncController` (`~/devices*`, `push-token`) |

### Points de conception

- **Le moteur offline n'est jamais dupliqué** — `SqlSyncService.ProcessAsync` gagne deux branches
  de dispatch (`delivery_stop`/`field_intervention`, opération `update`) qui appellent
  `IDeliveryService.CompleteStopAsync`/`IFieldInterventionService.CompleteAsync` ; la file
  (`sync_operation`), l'idempotence (`operation_uuid`) et les conflits (`sync_conflict`) restent
  strictement ceux de la Phase 10 — aucune deuxième table de queue pour le mobile.
- **`notification_outbox_dedup_uk` est `UNIQUE NULLS NOT DISTINCT (dedup_key, channel)`** — une
  ligne sans `dedup_key` (NULL) entre en collision avec **n'importe quelle autre ligne NULL du
  même canal**, pas seulement avec elle-même : `SendPushAsync` doit toujours fournir une clé
  fraîche (`Guid.NewGuid()`) par ligne quand la déduplication n'est pas voulue — bug réel détecté
  par un test d'intégration (deux tournées créées dans la même suite déclenchaient une violation
  de contrainte dès le deuxième envoi push, tous destinataires confondus).
- **Un appareil mobile est personnel, un poste POS est partagé** — `sync_device.user_id` n'est
  renseigné que pour `device_type = 'mobile'` ; un poste POS/desktop/serveur reste sans
  utilisateur, cohérent avec §18 (appareil partagé, changement d'utilisateur, aucun mélange de
  session) déjà assuré par l'authentification par requête, jamais par l'appareil lui-même.
- **`attachment` est délibérément pauvre** — un blob + son hash, rien de plus. La tentation de
  construire un mini-GED ici a été évitée : la Phase 45 est explicitement dédiée à ça (recherche,
  versioning, OCR, conservation). Construire plus ici aurait dupliqué du travail à la phase
  suivante.
- **Push ajouté sans rouvrir les préférences de notification (Phase 34)** — `SendPushAsync` est un
  envoi direct (pas de routage par `notification_preference`, qui n'a pas de colonne `push`) :
  contourne délibérément le système de préférences par catégorie plutôt que d'étendre son schéma/
  DTOs/écran de préférences pour une seule phase — documenté comme simplification assumée.
- **GPS capturé au point de preuve, jamais en continu** — `delivery_stop.latitude`/`longitude` ne
  sont écrits qu'à `CompleteStopAsync` (la preuve de livraison), pas à chaque changement de statut
  — conforme à §24 « pas de suivi permanent sans politique » par absence de toute table de trace
  continue, pas par un filtre applicatif qu'on pourrait contourner.

## Tests d'intégration — `MobileFieldOpsTests` (6, `[Collection("finance")]`, entreprise c2)

| Test | § | Vérifie |
|---|---|---|
| `Device_registration_links_mobile_user_and_accepts_push_token` | 20, 32 | un appareil `mobile` est lié à l'utilisateur appelant ; jeton push accepté ; fournisseur invalide rejeté |
| `Attachment_upload_and_read_roundtrip_rejects_bad_kind_and_bad_content` | 22-23 | upload/lecture fidèles (octets + hash), type invalide et contenu base64 invalide rejetés |
| `Delivery_route_creation_notifies_driver_and_stop_without_order_completes_with_proof` | 12, 20, 52 | création de tournée met en file une notification push vers le livreur ; complétion d'étape avec signature/photo/GPS ; tournée marquée close quand toutes ses étapes le sont |
| `Delivery_stop_replays_through_offline_sync_engine_with_dedup_and_conflict_on_reuse` | 25-29 | livraison rejouée via `ISyncService.PushBatchAsync` ; rejeu du même `operation_uuid` = dédup ; nouvelle opération sur une étape déjà livrée = conflit métier persisté |
| `Field_intervention_lifecycle_online_and_via_offline_sync` | 13, 25-29 | cycle complet en ligne (créer→démarrer→terminer) et une seconde intervention complétée via le moteur offline |
| `Mobile_bootstrap_pull_and_config_reflect_caller_scope` | 25, 40, 48 | le bootstrap ne renvoie que les étapes/interventions/tâches de l'appelant ; la config expose la politique offline et les types d'opération rejouables |

## Tests API — `MobileEndpointsTests` (3)

| Test | Vérifie |
|---|---|
| `Mobile_routes_require_authentication` | 4 routes `/api/v1/*` sans jeton → 401 |
| `Read_only_mobile_and_field_ops_endpoints_are_wired_over_http` | 12 endpoints de lecture → 200, y compris les alias `/devices` et `/sync/devices` en parallèle |
| `Attachment_upload_and_read_round_trip_over_http` | upload puis lecture d'une pièce jointe, type de contenu correct dans la réponse HTTP |

## Frontend

`src/mobile/mobileApi.ts` (types + fonctions) + `src/mobile/FieldOpsSections.tsx` (3 sections)
branchées dans **`FieldOpsPage.tsx`** (nouvelle page, route `/field-ops`, nav « Opérations »,
icône `MapPin` ajoutée à `shared/icons.tsx`, permission `field_ops.view`) :

- **Livraisons** — tournées du jour avec leurs étapes, actions de statut (en route/arrivé/livré/
  échec), création rapide d'une tournée à une étape (gardé par `field_ops.manage`) ;
- **Interventions** — liste, démarrer/terminer avec rapport et temps passé ;
- **Mobile** — politique offline effective (source Phase 10) et rapports de crash récents.

Vitest `mobileApi.test.ts` (4) → **85 tests frontend**.

## Manques assumés (backlog)

- Application mobile native/PWA réelle (§4) — le serveur est prêt (API + moteur offline
  extensible), aucun client natif n'existe encore pour l'exercer en conditions réelles
- Tableaux de bord mobiles dédiés dirigeant/manager/comptable (§7-9) — les dashboards existants
  (Phase 7) restent accessibles depuis n'importe quel appareil via le même frontend responsive
- Vrai fournisseur push (FCM/APNs/WebPush) — `LoggingPushChannel` reste un stub, comme les canaux
  email/sms/webhook depuis la Phase 34
- Regroupement/résumé des notifications intelligentes (§20, lié à Phase 43 §47)
- Rôles système dédiés « livreur »/« technicien »/« agent inventaire » (§5) — non modélisés, les
  permissions `field_ops.*` sont accordées aux rôles existants les plus proches
- Compression/reprise d'upload de pièces jointes (§36) — l'upload actuel est un appel unique
- Mise à jour versionnée réelle (force update/rollback, §45) — `minAppVersion` est une valeur
  statique en attendant un client à versionner
- Mobile analytics (§47), impression/périphériques mobiles pilotés serveur (§34-35, majoritairement
  client), MDM (§33, infrastructure tierce)
- Partage de document via mécanisme OS avec lien temporaire expirant (§42) — lié à la Phase 45 (GED)
- Généralisation de `attachment` en vraie gestion documentaire (recherche, versioning, OCR) —
  explicitement laissée à la Phase 45
