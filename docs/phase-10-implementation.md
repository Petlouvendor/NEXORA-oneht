# NEXORA — Phase 10 — État de l'implémentation

Référence : [`phase-10-local-offline-hybride-cloud.md`](phase-10-local-offline-hybride-cloud.md).

## Principe directeur

Le mode de déploiement ne change **pas** les règles métier (§2). Une opération créée hors
connexion est rejouée à la synchronisation via **le même parcours de vente**
(`SalesService.CheckoutAsync`) avec la **même idempotence** (`operation_uuid` unique global +
`source_operation_uuid` sur les entités). Aucune donnée n'est écrasée silencieusement : un
désaccord devient un **conflit** tracé, jamais une confirmation inventée (§15-16, §28).

## Ce qui est livré

### Base de données — `V029`

Réutilise le modèle posé en `V017` (`sync_device`, `sync_operation`, `sync_conflict`) et
l'enrichit.

| Objet | Rôle |
|---|---|
| `offline_policy` | politique par entreprise : ventes / crédit / remises / remboursements offline, plafond crédit offline, durée offline max, seuil d'alerte file, moyens de paiement autorisés, synchro auto (§10, §17) |
| `sync_batch` | lot de synchronisation reçu d'un appareil, avec compteurs confirmé / conflit / échec (§14, §30) |
| `sync_operation` | `+ priority` (0 = critique), `result_entity_id`, `result_summary`, `last_attempt_at`, `sync_batch_id` |
| `sync_conflict` | `+ conflict_type` (`stock`/`credit`/`duplicate`/`validation`/`period`/`business`), `detail`, `recommended_action`, `resolution_note` |
| permissions | `sync.{view,manage,devices}`, `offline.configure` + rattachement aux rôles |
| RLS | rattrapage `company_id` post-`V020` — 130/140 tables protégées (§19, §31) |

### Backend — `Nexora.Application/Sync` + `Nexora.Infrastructure/Sync`

```
ISyncService
  PushBatchAsync      reçoit un lot d'opérations offline, traite chacune de façon idempotente,
                      renvoie un résultat par opération (SYNCED / CONFLICT / FAILED) (§13-14)
  SyncCenterAsync     file, échecs, conflits ouverts, dernière synchro par appareil,
                      actions recommandées (§30)
  ResolveConflictAsync  décision + auteur journalisés ; l'opération devient PENDING (client_wins)
                      ou FAILED (server_wins/manual) (§15)
IDeviceRegistryService
  RegisterAsync / ListAsync / RevokeAsync (motif obligatoire) / HeartbeatAsync (§12, §31)
IOfflinePolicyService
  GetAsync / SetAsync (§10, §17)
```

**Traitement d'une opération (`PushBatchAsync`).** Appareil vérifié actif (un appareil
`revoked` → `403 sync.device_revoked`). Pour chaque opération, triée par priorité puis
séquence client : si `operation_uuid` déjà `SYNCED` → on renvoie le résultat mémorisé
(rejeu idempotent, §13) ; si déjà `CONFLICT` → on renvoie le conflit ; sinon on enregistre
`SYNCING` et on dispatche. Type pris en charge : **vente** (`sale`/`create`) — la charge
utile est un `CheckoutCommand`. Contrôle de politique : crédit offline interdit ou au-delà
du plafond → `CONFLICT` type `credit` (§17). Sinon `SalesService.CheckoutAsync` :
succès → `SYNCED` (`result_entity_id` = vente) ; `pos.insufficient_stock` → `CONFLICT`
type `stock` + action recommandée (§16) ; `pos.credit_limit` → `CONFLICT` type `credit` ;
toute autre erreur ou exception → `FAILED` (jamais de vente confirmée, §28). Chaque
opération est isolée : un échec n'interrompt pas le lot.

**Modification du noyau (Phase 6).** `SalesService.CheckoutAsync` renvoie désormais le code
`pos.insufficient_stock` (`Conflict`) pour une sortie de stock insuffisante — exploité par le
moteur de synchronisation pour classer un conflit de stock.

### API — `v1`

| Route | Permission |
|---|---|
| `POST /sync/batch` | `pos.sell` |
| `GET /sync/center` | `sync.view` |
| `POST /sync/conflicts/{id}/resolve` | `sync.manage` |
| `GET /sync/devices` · `POST .../devices/{id}/heartbeat` | `sync.view` |
| `POST /sync/devices` · `POST .../devices/{id}/revoke` | `sync.devices` |
| `GET /sync/offline-policy` | `sync.view` |
| `PUT /sync/offline-policy` | `offline.configure` |

### Frontend

`frontend/nexora-web/src/sync/SyncCenterPage.tsx` (route `/sync`, nav « Synchronisation ») :
tuiles file / synchronisées / échecs / conflits / dernière synchro, bandeau d'actions
recommandées, table des appareils (dernière synchro, vus, file, révocation), liste des
conflits avec action recommandée et résolution, panneau de la politique offline (bascules
selon `offline.configure`). `npm run typecheck` + `build` OK.

### Démo

`demo_abc_group.sql` : `offline_policy` par défaut (comptant OK, crédit refusé) pour les
3 entreprises ; la caisse T1 de la Pharmacie enregistrée comme `sync_device` actif. Rejouable.

## Vérifications exécutées

**Tests d'intégration `Nexora.IntegrationTests/OfflineSyncTests` — 8/8** :

| Scénario (§35) | Vérifié |
|---|---|
| Enregistrer / lister / révoquer un appareil | appareil `revoked` → `PushBatch` refusé `sync.device_revoked` ; motif de révocation obligatoire |
| Lot de ventes offline synchronisé | 2 opérations → 2 `SYNCED`, 2 ventes réelles, stock décrémenté |
| Envoyer deux fois le même lot | 2ᵉ envoi → `SYNCED` avec les mêmes identifiants ; **1 seule** vente / 1 seul mouvement de stock par `operation_uuid` |
| Conflit de stock | 1 seul article en stock, 2 opérations → 1 `SYNCED`, 1 `CONFLICT` type `stock` + `sync_conflict` ; l'article n'est **pas vendu deux fois** |
| Crédit offline selon politique | politique interdit → `CONFLICT` type `credit` ; politique autorise (dans le plafond) → `SYNCED` |
| Centre de synchronisation | synchronisées > 0, appareil listé, dernière synchro renseignée |
| Résoudre un conflit | résolution `server_wins` + note enregistrées avec l'auteur ; 2ᵉ résolution → refus |
| Une opération non persistée n'est jamais confirmée | produit inexistant → `FAILED`, **aucune vente** ; rejeu → toujours `FAILED` |

Smoke API (démo) : `offline-policy` GET/PUT, `devices` list / register / heartbeat / revoke,
`center`, push depuis un appareil révoqué → **403** ; caissier voit le centre (200) mais ne
configure pas la politique (403).

**Total backend : 80 tests verts** (16 unitaires + 2 API + 62 intégration), build Release 0 warning.
DB : 29 migrations (V001–V029), **140 tables**, 130 avec RLS. Rebuild + smoke + démo (rejouée) OK.

## Écarts et décisions

- **Types d'opérations synchronisées** : la **vente** (opération offline critique, §9) est
  pleinement prise en charge. Les autres types (`payment`, `cash_movement`, `sale_return`)
  sont marqués `FAILED` avec un message explicite — à ajouter au fil des besoins ; leur
  idempotence est déjà portée par `source_operation_uuid` côté noyau.
- **Estimation du crédit offline** : calculée depuis les prix de la charge utile
  (`unitPriceOverride`) — le POS offline calcule ses prix (§9). Sans prix dans la charge
  utile, la vente est traitée comme comptant.
- **Résolution de conflit** : enregistre la décision et son auteur et repositionne
  l'opération (`PENDING` pour rejeu / `FAILED`). La correction métier associée (retour,
  écriture d'ajustement) reste une action manuelle tracée (§16).
- **Sauvegardes, restauration, migrations, monitoring, périphériques, mode dégradé**
  (§19-25, §28-29, §32) : **définis** dans le document de phase et la documentation
  d'exploitation ; ce sont des procédures d'infrastructure, pas du code applicatif.
  `docker-compose`, `rebuild.sh`, `apply.sh` (suivi `schema_migration` + checksum) couvrent
  l'installation et les migrations DB.
- **Chiffrement du stockage local, révocation cryptographique, identité d'appareil signée**
  (§31) : `sync_device.public_key` est en place ; la vérification de signature des lots est
  reportée.
- **`IdempotencyFilter` HTTP** (Phase 3) reste un cache mémoire ; l'idempotence fiable de la
  synchronisation passe par `operation_uuid` en base.

## Couverture des critères de sortie (§36)

| Critère | État |
|---|---|
| Modes local / cloud / hybride | ✅ `company.deployment_mode` + `docker-compose` + documentation ; même produit |
| Offline / queue / synchronisation | ✅ `sync_batch` / `sync_operation` / `ISyncService.PushBatchAsync` |
| Identifiants / idempotence | ✅ `operation_uuid` unique global + rejeu idempotent testé |
| Conflits | ✅ `sync_conflict` typé (stock / crédit / …), jamais d'écrasement silencieux |
| Paiements offline | ✅ `offline_policy.allowed_payment_methods` ; un paiement non confirmé n'est pas présenté comme encaissé (statut `payment.status`) |
| Sauvegardes / restauration / migrations | ⚙️ procédures d'infrastructure définies (`rebuild.sh`, `apply.sh`, `docker-compose`) |
| Multi-sites | ✅ établissements + stock/caisse locaux (Phase 3-6) + synchro |
| Monitoring / centre de synchronisation | ✅ `SyncCenterAsync` + écran ; monitoring système = exploitation |
| Sécurité offline | ✅ appareils enregistrés + révocables, politique configurable, permissions dédiées |
| Périphériques | ⚙️ défini (couche d'intégration matérielle) — hors code cœur |
| Scénarios testables | ✅ 8 scénarios §35 automatisés |

## Suite — Phase 11

Sécurité, Audit, Conformité & Anti-fraude : accès, audit immuable, contrôles financiers,
prévention des abus, traçabilité et protection des données.
