# Phase 45 — GED, documents, archivage & signature — implémentation

**Statut : livré et vérifié.** Migration `V058` (7 tables neuves, 5 permissions, 2 jobs
planifiés). 6 tests d'intégration (`GedDocumentsTests`), 3 tests API (`GedEndpointsTests`),
4 tests frontend (`gedApi.test.ts`).
**340 tests backend** verts (18 unit + 38 API + 284 intégration), Debug **et** Release, 0 warning,
vérifiés après un rebuild complet de la base (`V001`→`V058`, 242 tables). 89 tests frontend.
`tsc` + `vite build` verts.

Deux concepts voisins existaient déjà et restent **validés**, volontairement **non fusionnés** :
`issued_document` (Phase 30) — documents que NEXORA ONE **émet** (facture/devis/bon…), numérotés,
figés par un hash, vérifiables publiquement ; `attachment` (Phase 44) — blob minimal photo/
signature pour la preuve terrain, explicitement annoncé comme devant être « généralisé par la
Phase 45 ». Cette phase ajoute la bibliothèque documentaire elle-même : des documents **reçus/
importés/scannés** (factures fournisseurs, contrats, justificatifs, pièces comptables), avec
classification, versions, liens multiples, recherche, verrouillage, signature multi-signataires,
partage à lien temporaire, et une politique de **rétention dédiée aux documents métier** —
précisément ce que `retention_policy` (Phase 41) exclut par construction.

### Socle réutilisé

- **Phase 11** — `audit_log` immuable → historique GED (§45), rien de dupliqué
- **Phase 23** — `pg_trgm`/`unaccent` (déjà activés) disponibles pour une recherche tolérante future
- **Phase 25** — non touché structurellement, mais `sales_order` reste le lien naturel (`document.reference_type`) pour une facture liée à une commande
- **Phase 30** — `IDocumentService`/`IDocumentQueries`/`issued_document` **validés**, non reconstruits ; collision de route découverte et corrigée (voir Points de conception)
- **Phase 34** — non réutilisé directement cette phase (pas de notification automatique sur événement GED — backlog)
- **Phase 41** — `retention_policy` confirmé **structurellement incompatible** par conception (allowlist fermée à 4 types techniques) : `document_retention_policy` est une table sœur, jamais une extension de celle-ci
- **Phase 44** — `attachment` validée et **non touchée** ; `document`/`document_version` sont un concept parallèle, pas son remplacement

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2 | Principe (import→classification→indexation→OCR→validation→version→signature→archivage→recherche/audit) ; document original identifiable ; métadonnées ne remplacent jamais la pièce source | `document`/`document_version` — le fichier vit dans `document_version.content` (jamais dans les métadonnées), le pipeline est porté par le cycle de statut + les actions dédiées (`SubmitForReviewAsync`→`ApproveAsync`→…) |
| 3-5 | Types de documents ; classement (entreprise/établissement/type/année/client/fournisseur/tags…) ; métadonnées | `document.document_type` (18 valeurs couvrant §3), `category`/`tags` (jsonb, indexé GIN)/`fiscal_year`/`document_date`/`customer_id`/`supplier_id`/`reference_type`+`reference_id`. Établissement/projet — colonne prête (`establishment_id`), pas encore alimentée par un flux dédié — **backlog** |
| 6 | Documents liés à plusieurs objets, liens traçables, suppression d'un objet métier ne supprime jamais une pièce conservée | `document_link` (table dédiée, many-to-many) en plus du lien principal `reference_type`/`reference_id` ; aucune contrainte `ON DELETE CASCADE` depuis les tables métier vers `document` — un document survit structurellement à la suppression de l'objet qu'il documentait |
| 7-10 | Upload (glisser-déposer/caméra/lot/reprise) ; formats (PDF/JPG/PNG/WebP/TIFF/DOCX/XLSX/CSV) ; limites de taille/quotas ; sécurité fichiers (antivirus/MIME réel/stockage privé/chiffrement) | `CreateAsync`/`AddVersionAsync` valident un allowlist de 9 types MIME (§8) et une taille 1 octet-25 Mo (§9, `CHECK` en base). **Backlog explicite, documenté honnêtement** : glisser-déposer/lot/reprise sont des choix frontend non construits (un seul fichier à la fois cette phase) ; **aucun scan antivirus ni vérification MIME réelle** (le `Content-Type` déclaré est fait confiance, pas inspecté) — nécessiterait une dépendance externe absente de l'environnement ; chiffrement au repos = infrastructure (PostgreSQL), hors périmètre applicatif |
| 11-12, 38 | OCR (texte/langue/n° facture/date/fournisseur/montant/taxes/devise/confiance) ; workflow OCR→champs proposés→validation→facture ; suggestion fournisseur/doublon | **`IDocumentOcrService` — heuristique minimal, PAS un moteur OCR** (backlog explicite et assumé) : extrait le texte tel quel pour un contenu déjà textuel (CSV), marque `unsupported` pour tout binaire (PDF/image) plutôt que d'inventer une extraction. `ocr_extracted` reste vide dans ce cas — jamais une donnée inventée pour alimenter une facture (§11, avertissement du cahier des charges pris au pied de la lettre) |
| 13-14 | Recherche (nom/référence/texte OCR/client/fournisseur/n°/date/montant/tags) ; recherche avancée (AND/OR/phrase exacte/plage/statut/version/signé) | `IGedService.SearchAsync(DocumentSearchQuery)` — texte (ILIKE nom/catégorie/texte OCR), type, statut, client, fournisseur, plage de dates, tags (`jsonb @>`), signé/non signé. AND implicite entre critères (pas de constructeur booléen OR/parenthèses, pas de recherche par montant faute d'un champ montant sur `document`) — **backlog** ; filtres enregistrés déjà couverts génériquement par `saved_filter` (Phase 40), non redistribués |
| 15 | Aperçu (PDF viewer/zoom/rotation/miniatures/pages/téléchargement) | **Backlog frontend explicite** — `GET /ged-documents/{id}/content` sert déjà le fichier brut (téléchargement selon permission, §15 dernier point) ; visualiseur PDF/zoom/miniatures sont des composants client non construits |
| 16-17 | Versioning (auteur/date/motif/version active/restaurer) ; historique (créé/modifié/consulté/téléchargé/partagé/signé/approuvé/archivé/restauré/supprimé) | `document_version` — jamais écrasée, `change_reason`, `document.current_version_id` = version active. Historique **déjà couvert par `audit_log`** (§45, `trg_audit_*`) plutôt que dupliqué dans une table dédiée ; consultation/téléchargement ne sont **pas** audités individuellement (uniquement les mutations) — **backlog** si un audit de lecture devient nécessaire |
| 18 | Verrouillage (en validation/signé/clôturé/métier, déverrouillage par permission) | `document.locked`/`locked_reason` — posé automatiquement à la demande de signature et à la complétion (§19-22), manuellement via `LockAsync`/`UnlockAsync` (`ged.manage`) ; bloque `AddVersionAsync`/`UpdateMetadataAsync` |
| 19-22 | Signature (simple/multiple, ordre, invitation, rappel, expiration, statut) ; workflow (signataires→invitation→authentification→signature→preuve→final→archivage) ; signataires (interne/externe/ordre/rôle/échéance/relance) ; preuve (identité/authentification/hash/version/journal) | **`ISignatureService`** — `document_signature_request`/`document_signer`, ordre strictement respecté (`sequence_no`, un signataire ne peut signer tant qu'un rang antérieur reste `pending`), preuve capturée server-side (identité déclarée = utilisateur NEXORA authentifié, hash de la version courante, horodatage — `document_signer.proof` jsonb). **Toujours qualifiée de « signature simple »** — aucune signature avancée/qualifiée d'un prestataire tiers (§19, avertissement pris au pied de la lettre : NEXORA ne prétend à aucune valeur juridique particulière). Invitation/rappel par e-mail — **backlog** (aucun canal externe déclenché, la liste des demandes est consultable dans l'app) ; expiration automatique via le planificateur (`ged.expire_signatures`) |
| 23-24 | Approbation interne (dépense/achat/contrat/facture/remboursement/pièce comptable, par montant/rôle) ; workflow (brouillon→revue→approbation→signature→final→archive) | `document.status` porte le cycle littéralement : `draft→pending_review→approved→[signed]→archived`, `SubmitForReviewAsync`/`ApproveAsync`(4 yeux, déposant ≠ approbateur, comme Phase 11)/`RejectAsync`. Workflow **par montant** — non applicable, un document GED n'a pas de montant propre (contrairement à une dépense Phase 14) ; workflow par rôle — assuré par les permissions `ged.manage`/`ged.sign` |
| 25-26 | Documents financiers (factures/avoirs/reçus/justificatifs/relevés/pièces caisse/banque/rapprochement/fiscaux) ; documents comptables (pièces journal/justificatifs/GL/balance/déclarations/clôture/inventaire) | Couverts par les valeurs de `document_type` (§3) ; le lien vers l'écriture/la pièce source se fait via `reference_type`/`reference_id` ou `document_link` (§6) — aucune duplication des montants/écritures elles-mêmes (qui restent dans `journal_entry`, Phase 29) |
| 27-29 | Archivage (actif/archivé/gelé/rétention expirée/éligible/legal hold) ; rétention (politique globale/entreprise/type/durée/exception/audit) ; legal hold (blocage/motif/périmètre/audit) | **`document_retention_policy`** — dédiée aux documents métier, gabarits système (`company_id` NULL, comme `sector_profile`) amorcés pour 5 types (factures/pièces comptables/fiscal 10 ans, contrat 5 ans, défaut général 3 ans) + surcharge par entreprise. `IDocumentRetentionService.ApplyRetentionAsync` (job planifié `ged.apply_retention`) calcule `document.retention_until` ; `legal_hold`/`legal_hold_reason` bloquent **toute** suppression, y compris une rétention par ailleurs échue |
| 30 | Suppression (soft delete/corbeille/validation/purge définitive/journal/protection rétention-hold) | `RequestDeletionAsync`→`ConfirmDeletionAsync` (4 yeux, demandeur ≠ confirmeur) → suppression physique de la ligne `document` (la ligne `document_version` reste en base via un `ON DELETE CASCADE`, ce n'est jamais un soft-delete flag séparé) ; bloquée par `legal_hold` ou `retention_until` non échue à **chaque** étape (demande **et** confirmation, pas seulement à la demande) ; `CancelDeletionRequestAsync` annule une demande |
| 31 | Partage (interne/externe/lien temporaire/expiration/téléchargement contrôlé/lecture seule/mot de passe/révocation/audit) | **`document_share`** — jeton aléatoire 24 octets, expiration obligatoire, mot de passe optionnel (haché), `allow_download` (lecture seule sinon), révocable. Accès public par jeton **contourne délibérément la RLS** (`tenant.BeginSystemScope()`, même principe que `DocumentsController.Verify` Phase 30) puisqu'un destinataire externe n'a aucun contexte entreprise NEXORA |
| 32 | Dossiers (personnalisés/sous-dossiers/favoris/partage/permissions héritées) | **Backlog explicite** — la classification actuelle repose sur type/catégorie/tags (§4-5), pas une arborescence de dossiers ; `saved_filter` (Phase 40) peut déjà simuler des « vues » sauvegardées en attendant |
| 33-35 | Permissions (lecture/aperçu/téléchargement/création/modification/version/signature/partage/suppression/archivage/administration) ; scope (groupe/entreprise/établissement/département/utilisateur/objet) ; GED multi-entreprises (bibliothèques séparées, aucune fuite, archivage indépendant) | 5 permissions `ged.{view,upload,manage,sign,manage_retention}` couvrant les 11 actions du §33 par regroupement pragmatique (comme `field_ops`/`intelligence` aux phases précédentes) plutôt que 11 permissions unitaires. RLS entreprise stricte sur les 7 tables (§35) — aucune bibliothèque groupe transverse cette phase (**backlog**, le scope reste entreprise par entreprise, cohérent avec §35 « aucune fuite entre tenants ») |
| 36-37 | Documents offline (capture locale/compression/chiffrement cache/sync/reprise) ; mobile (scan caméra/photo/signature/recherche/aperçu/upload/validation/notifications) | **Non dupliqué** — Phase 44 a déjà posé `attachment` + le moteur offline étendu pour la capture terrain (photo/signature de livraison/intervention) ; la bibliothèque GED elle-même n'a pas de chemin offline dédié cette phase (upload direct uniquement) — **backlog** si la capture terrain doit un jour alimenter directement `document` plutôt que `attachment` |
| 39 | Détection doublons (hash/n°/tiers/date/montant/score/alerte/décision utilisateur) | Détection par **hash SHA-256** à chaque création/version (`FindDuplicateAsync`) — jamais bloquante, `UploadResultDto.PotentialDuplicateOf` signale, la décision reste humaine (§39 littéral). Score composite (n°+tiers+date+montant) — **backlog**, le hash exact couvre le cas le plus fiable (fichier strictement identique) |
| 40-44 | Documents générés (factures/reçus/devis/rapports/états/attestations/exports) ; templates (logo/en-tête/numérotation/langue) ; impression ; watermark ; export (PDF/ZIP/CSV/API) | **Déjà couverts par `issued_document`/`IDocumentService`** (Phase 30, génération/rendu/numérotation) — non redistribués, cette phase ne génère aucun document, elle les **archive**. Watermark, export ZIP contrôlé — **backlog explicite** |
| 45 | Audit GED (upload/lecture/téléchargement/partage/modification/version/signature/approbation/archivage/suppression/permission) | **`IGedAuditQuery`** — lit `audit_log` (immuable, Phase 11) filtré aux 7 tables de ce module, aucune duplication. Lecture/téléchargement individuels ne sont **pas** journalisés (seulement les mutations, via `trg_audit_*`) — **backlog** si un vrai « qui a consulté quoi » devient nécessaire |
| 46 | Intégrité (hash/taille/version/source/date/contrôle/détection corruption) | `document_version.sha256_hash`/`size_bytes` calculés à l'écriture ; aucun contrôle périodique de ré-vérification d'intégrité (relire et recomparer le hash) — **backlog**, cohérent avec l'absence de stockage externe à vérifier (le contenu vit dans PostgreSQL, protégé par ses propres mécanismes) |
| 47-48 | Backup documents (séparé/chiffré/versionné/RPO-RTO) ; migration documents (import ancien système/mapping/OCR optionnel/rapport) | Backup — **hérité** du backup PostgreSQL global (Phase 36, `IContinuityService`) puisque `document_version.content` vit dans la même base ; pas de backup de fichiers séparé (cohérent avec le choix de stockage en base plutôt qu'un stockage objet externe). Migration — **backlog explicite**, aucun connecteur d'import de GED tierce construit cette phase |
| 49 | API principales | Toutes servies — voir § Backend/API. **Déviation documentée** : `/documents` appartient déjà à `DocumentsController` (Phase 30) — la bibliothèque GED est exposée sous `/ged-documents` (voir Points de conception) |
| 50 | Événements | Portés par les colonnes de statut (`document.status`, `document_signature_request.status`, `document_share.revoked_at`) + `audit_log` (trigger) — cohérent avec le style déjà en place, pas de bus d'évènements séparé |
| 51 | Tests | `GedDocumentsTests` (6) + `GedEndpointsTests` (3) — cf. ci-dessous |
| 52-53 | Scénario facture fournisseur ; scénario contrat | Le premier est directement reproduit (upload→OCR→doublon→revue→approbation, `Document_create_detects_duplicate_versions_history_and_ocr_stub` + `Document_workflow_requires_four_eyes_and_lock_blocks_new_version`) ; le second par `Signature_workflow_enforces_order_locks_document_and_supports_decline` (import→signataires→signatures→verrouillage final) — **alerte d'échéance de contrat** (§53 dernière étape) non construite, backlog |
| 54 | Critères de sortie | Classement/métadonnées/liens/versioning/verrouillage/signature+preuves/approbation/rétention/legal hold/suppression contrôlée/partage/permissions/scope/doublons/audit **livrés** ; upload avancé (glisser-déposer/lot/reprise/scan antivirus/MIME réel), OCR réel, aperçu visuel, dossiers, GED multi-entreprises transverse, offline GED dédié, templates/watermark/export avancé, backup séparé, migration : **backlog explicite**, détaillé ci-dessus |
| 55 | Étape suivante — Phase 46 (API publique, intégrations, écosystème) | hors périmètre |

## `V058` — contenu (7 tables neuves, 5 permissions, 2 jobs)

- **`document`** (NEW, RLS entreprise) — classification (type/catégorie/tags jsonb GIN)/liens
  (`reference_type`/`reference_id`)/cycle de statut (`draft→pending_review→approved→signed→
  archived→frozen`)/verrouillage/legal hold/rétention/suppression à 4 yeux.
- **`document_version`** (NEW, RLS entreprise) — le fichier lui-même (`bytea` ≤ 25 Mo), jamais
  écrasé, `UNIQUE(document_id, version_no)`, hash SHA-256 indexé (§39 doublons), champs OCR.
- **`document_link`** (NEW, RLS entreprise) — liens multiples vers des objets métier (§6),
  `UNIQUE(document_id, linked_type, linked_id)`.
- **`document_share`** (NEW, RLS entreprise) — jeton unique, expiration obligatoire, mot de passe
  optionnel, révocable.
- **`document_signature_request`** + **`document_signer`** (NEW, RLS entreprise) — ordre
  (`sequence_no`), preuve jsonb, signataire interne (`user_id`) ou externe (`external_name`/
  `external_email`).
- **`document_retention_policy`** (NEW, `company_id` nullable = gabarit système comme
  `sector_profile`) — 6 gabarits amorcés, distincte et complémentaire de `retention_policy`
  (Phase 41, réservée aux données techniques).
- `trg_audit_*` sur les 7 tables. Permissions **`ged.{view,upload,manage,sign,manage_retention}`**.
  Jobs planifiés **`ged.apply_retention`** et **`ged.expire_signatures`** (par entreprise).
- **242 tables (+7), 207 RLS (+7)**, 58 migrations (V001–V058).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Ged/GedContracts.cs` |
| Application | `Nexora.Application/Ged/IGed.cs` — `IGedService`, `ISignatureService`, `IDocumentOcrService`, `IDocumentRetentionService`, `IGedAuditQuery` ; `PermissionCatalog.Ged` ; `Notifications.INotificationService` (inchangée), `Sync.IDeviceRegistryService` (inchangée) |
| Infrastructure | `Ged/{GedService,SignatureService,DocumentOcrService,DocumentRetentionService,GedAuditQuery}.cs` ; `SchedulerRunner` += 2 jobs |
| API | `Nexora.Api/Controllers/GedController.cs` — `/api/v1/{ged-documents/*,document-workflows,document-shares,signature-requests,document-retention-policies,document-audit,shared/{token}}` |

### Points de conception

- **Collision de route découverte et corrigée — `/documents` appartient déjà à Phase 30** :
  `POST /api/v1/documents` (mon `GedController.Create`) entrait en conflit exact avec
  `DocumentsController.Issue` (Phase 30), provoquant une `AmbiguousMatchException` (500) détectée
  par le premier test API. Corrigé en déplaçant **toute** la ressource GED sous `/ged-documents`
  (`document-workflows`/`document-shares`/`signature-requests`/`document-retention-policies`/
  `document-audit`/`shared/{token}` ne collisionnaient pas et restent tels quels) — même principe
  que Phase 42 évitant `/kpis` (déjà pris par Phase 31) au profit de `/kpi-definitions`.
- **Accès public par jeton = `tenant.BeginSystemScope()` obligatoire** — `GetSharedContentAsync`
  échouait silencieusement en « lien invalide » (404) tant que la RLS entreprise s'appliquait à un
  appelant anonyme sans aucun contexte d'entreprise (`current_company_ids()` vide). Corrigé en
  reproduisant exactement le motif déjà établi par `DocumentsController.Verify` (Phase 30) :
  englober la lecture dans `tenant.BeginSystemScope()`, le jeton lui-même étant l'unique
  autorisation (pas la RLS).
- **`document` et `attachment` restent deux primitives séparées, pas une hiérarchie** —
  `attachment` (Phase 44) continue de servir la capture terrain (photo/signature de livraison/
  intervention, référencée par `sync_operation`) ; `document`/`document_version` servent la
  bibliothèque gouvernée (classification, versions, workflow, signature, rétention). Les fusionner
  aurait exigé de réécrire le moteur offline (Phase 44) pour un bénéfice marginal — documenté
  comme un choix délibéré, pas un oubli.
- **OCR : un heuristique honnête, jamais une fausse promesse** — `IDocumentOcrService` ne prétend
  jamais extraire un texte qu'il ne peut pas lire : `unsupported` pour tout binaire, `ocr_extracted`
  reste vide dans ce cas. Le cahier des charges (§11) exige explicitness que les champs extraits
  soient validés par un humain — plus facile à respecter quand l'extraction elle-même ne feint pas
  d'exister.
- **Rétention documentaire dédiée, jamais une extension de `retention_policy` (Phase 41)** — cette
  dernière ferme volontairement sa liste à 4 types techniques pour ne **jamais** toucher à une
  donnée métier ; `document_retention_policy` est une table sœur avec son propre gabarit
  nullable-`company_id`, jamais un assouplissement de l'allowlist existante.
- **Suppression bloquée à *chaque* étape, pas seulement à la demande** — `RequestDeletionAsync`
  **et** `ConfirmDeletionAsync` revérifient tous deux `legal_hold`/`retention_until`, au cas où un
  legal hold serait posé *entre* la demande et sa confirmation (§29 : blocage absolu, jamais
  contournable par un simple découpage en deux étapes).

## Tests d'intégration — `GedDocumentsTests` (6, `[Collection("finance")]`, entreprise c2)

| Test | § | Vérifie |
|---|---|---|
| `Document_create_detects_duplicate_versions_history_and_ocr_stub` | 7, 16, 39 | doublon détecté par hash (non bloquant) ; OCR complet sur CSV, `unsupported` sur PDF binaire ; versions multiples préservées et individuellement lisibles ; audit journalisé |
| `Document_workflow_requires_four_eyes_and_lock_blocks_new_version` | 18, 23-24 | déposant ne peut pas approuver son propre document ; verrouillage bloque une nouvelle version ; rejet ramène en brouillon |
| `Document_search_filters_by_type_tags_text_and_archive_restore` | 13-14, 27 | recherche par type/tag/texte/plage de dates ; archivage/restauration |
| `Document_links_and_share_enforce_password_expiry_and_revocation` | 6, 31 | liens multiples ; partage : mauvais mot de passe refusé, expiration effective, révocation effective |
| `Signature_workflow_enforces_order_locks_document_and_supports_decline` | 19-22 | ordre des signataires strictement respecté ; verrouillage pendant et après signature ; refus annule la demande |
| `Retention_policy_legal_hold_blocks_deletion_and_confirmation_requires_four_eyes` | 27-29 | surcharge de politique par type ; calcul de `retention_until` ; legal hold bloque la suppression même rétention échue ; confirmation à 4 yeux |

## Tests API — `GedEndpointsTests` (3)

| Test | Vérifie |
|---|---|
| `Ged_routes_require_authentication` | 4 routes `/api/v1/*` sans jeton → 401 |
| `Read_only_ged_endpoints_are_wired_over_http` | 6 endpoints de lecture → 200 |
| `Document_create_content_and_share_round_trip_over_http` | création → contenu → versions → partage → téléchargement public par jeton, bout en bout |

## Frontend

`src/ged/gedApi.ts` (types + fonctions, chemin `/ged-documents` documenté explicitement pour
éviter toute confusion avec `/documents`) + `src/ged/GedSections.tsx` (5 sections) branchées dans
**`GedPage.tsx`** (nouvelle page, route `/ged`, nav « Opérations », icône `Folder` ajoutée à
`shared/icons.tsx`, permission `ged.view`) :

- **Bibliothèque** — import de fichier (lecture locale en base64), recherche par texte/type,
  actions de cycle de vie (soumettre/approuver/archiver/restaurer/verrouiller/OCR/partager) ;
- **Revue & approbation** — file d'attente des documents en revue ;
- **Signatures** — demandes en cours, signer/refuser par signataire (ordre affiché) ;
- **Rétention & suppression** — politiques (gabarit système vs surcharge entreprise), documents
  éligibles à la suppression avec demande/confirmation ;
- **Audit** — journal filtré aux tables GED.

Vitest `gedApi.test.ts` (4, dont un test dédié confirmant l'usage de `/ged-documents` plutôt que
`/documents`) → **89 tests frontend**.

## Manques assumés (backlog)

- Upload avancé : glisser-déposer, upload par lot, reprise après interruption (§7) — un fichier à
  la fois cette phase
- Sécurité fichiers réelle : scan antivirus, vérification MIME par inspection du contenu (§10) —
  le `Content-Type` déclaré est actuellement fait confiance
- OCR réel (extraction sur image/PDF binaire, détection langue, champs facture) — heuristique
  minimal seulement, backlog explicite et assumé dès la conception (§11-12, §38)
- Recherche booléenne avancée (AND/OR explicites, phrase exacte, recherche par montant, §14)
- Aperçu visuel (PDF viewer, zoom, rotation, miniatures, §15) — téléchargement brut seulement
- Dossiers personnalisés/sous-dossiers/favoris (§32) — classification par type/catégorie/tags
- Invitation/rappel de signature par e-mail (§19, §21) — la demande est consultable dans l'app,
  aucun canal externe déclenché
- GED multi-entreprises transverse (bibliothèque groupe, §35) — scope entreprise par entreprise
- Chemin offline dédié pour la GED (§36-37) — la capture terrain passe par `attachment` (Phase 44)
- Détection de doublon par score composite (n°/tiers/date/montant, §39) — hash exact seulement
- Watermark, export ZIP/CSV avancé, impression pilotée (§40-44) — génération de documents déjà
  couverte par Phase 30, non étendue ici
- Audit de lecture/téléchargement individuel (§45, §17) — seules les mutations sont journalisées
- Contrôle périodique d'intégrité / détection de corruption (§46)
- Backup de documents séparé du backup PostgreSQL global (§47) — hérité de Phase 36
- Migration depuis un ancien système de GED (§48)
- Alerte d'échéance de contrat (§53, dernière étape du scénario)
