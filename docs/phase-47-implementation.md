# Phase 47 — Sécurité, IAM & Conformité — implémentation

**Statut : livré et vérifié.** Migration `V060` (4 tables neuves, 2 colonnes sur `user_session`,
1 colonne + extension de CHECK sur `continuity_incident`, 2 permissions, 15 contrôles de
conformité amorcés). 8 tests d'intégration (`SecurityGovernanceTests`), 4 tests API
(`SecurityGovernanceEndpointsTests`), 7 tests frontend (`securityGovernanceApi.test.ts`).
**362 tests backend** verts (18 unit + 46 API + 298 intégration), 0 warning, vérifiés après un
rebuild complet de la base (`V001`→`V060`, 249 tables). 103 tests frontend. `tsc` vert.

Le socle IAM/sécurité existait déjà et reste **validé**, volontairement **non reconstruit** :
Phase 11 (RBAC, `audit_log` immuable, moteur anti-fraude, workflow d'approbation générique à
4 yeux, sessions, politique de sécurité par entreprise), Phase 22 (affectations de rôle
temporaires/révocables — le mécanisme JIT/JEA du §47), Phase 33 (MFA TOTP + codes de récupération,
verrouillage progressif), Phase 35 (`api_key`, secret jamais en clair), Phase 36
(`continuity_incident`). Cette phase ajoute ce qui manquait réellement : un accès d'urgence
« break-glass » qui **réutilise** le moteur JIT plutôt que d'en inventer un second, un registre de
vulnérabilités et un inventaire de certificats, un registre de conformité **auto-évalué et
honnête**, l'extension du registre d'incidents existant à un second domaine (sécurité), un score
et un tableau de bord calculés à la lecture, et deux corrections de sécurité réelles (chiffrement
du secret MFA au repos, en-tête CSP).

### Socle réutilisé

- **Phase 3** — `/api/v1`, en-têtes de sécurité (`SecurityHeadersMiddleware`) → étendus, pas reconstruits
- **Phase 11** — `audit_log`/`IAuditQueryService` (généricité confirmée — interrogeable sans wrapper
  dédié), `approval_policy`/`IApprovalWorkflowService` (workflow générique, **non réutilisé** pour
  le break-glass par choix délibéré — voir Points de conception), `user_session`/
  `ISessionSecurityService` **étendus** pour devenir le registre d'appareils, `security_policy`
- **Phase 22** — `user_role.valid_from/valid_until/reason` + `UserAssignmentService.GrantAsync`/
  `RevokeAsync` — **le cœur du mécanisme break-glass**, aucune duplication
- **Phase 33** — `IMfaService`/TOTP/codes de récupération **validés**, `mfa_secret` désormais
  chiffré au repos par un composant additionnel (`IMfaSecretProtector`) sans changer le service
- **Phase 35** — `api_key` non touché ; convention de configuration (`Jwt:SigningKey` → repli
  développement nommé) reproduite pour `Security:MfaEncryptionKey`
- **Phase 36** — `continuity_incident`/`IContinuityService` **étendus** (colonne `domain`), jamais
  un second registre d'incidents
- **Phase 38** — `platform.security` (déjà seedée, déjà distribuée aux rôles plateforme) réutilisée
  telle quelle pour gater les 3 nouveaux registres plateforme ; `support_access_grant` **examiné et
  laissé tel quel** — voir Manques assumés

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 3-5 | IAM (utilisateurs/groupes/rôles/permissions/scopes/service accounts/appareils/sessions) ; RBAC (7 niveaux de rôle) | **Hérité et validé** (Phase 3/11/22) — `access_scope` couvre déjà PLATFORM/GROUP/COMPANY/ESTABLISHMENT/STORE/WAREHOUSE/POS_TERMINAL (7 niveaux, « caisse »=POS_TERMINAL, « entrepôt »=WAREHOUSE). **Niveau département absent** du schéma — backlog explicite (changement structurel hors périmètre de cette phase) |
| 6 | Séparation des responsabilités (créateur≠approbateur, actions critiques à double validation) | **Hérité** (Phase 11 `IApprovalWorkflowService`, garde générique requester≠approver) + **nouvelle garde dédiée** pour le break-glass (`emergency_access_grant_self_decision_chk`, CHECK **en base** en plus du contrôle applicatif — défense en profondeur, jamais contournable même par un super-administrateur) |
| 7-9 | Authentification (mot de passe fort/MFA/passkeys/OTP/SSO/reset/verrouillage) ; MFA (authenticator/passkey/clé sécurité/récupération/obligatoire admin/adaptatif) ; mot de passe (hash moderne, pas de clair) | **Hérité** (Phase 33 TOTP + 10 codes de récupération à usage unique, BCrypt facteur 12, verrouillage 5 tentatives/15 min). **Nouveauté** : secret TOTP **chiffré au repos** (AES-256-GCM, `IMfaSecretProtector`) — corrige un écart réel où `mfa_secret`/`mfa_pending_secret` étaient stockés en clair malgré un commentaire de code affirmant le contraire. Passkeys/WebAuthn, SSO — **backlog explicite**, aucune dépendance de cérémonie WebAuthn ajoutée cette phase |
| 10-11 | Sessions (expiration/rotation/révocation/déconnexion globale/device binding/CSRF) ; appareils (enregistrement/nom/type/dernière activité/révocation/confiance/sessions liées) | **Hérité** (Phase 11 `user_session`, déconnexion globale via `security_epoch`). **Nouveauté** : un appareil **EST** une session — `device_type` (inféré du user-agent) et `trusted` (bool) ajoutés à `user_session` plutôt qu'une table séparée ; `GET/POST /security/devices*` sont des projections du même registre. CSRF — non applicable (API à jetons `Authorization: Bearer`, aucune authentification par cookie) |
| 12 | Accès d'urgence (break-glass : motif obligatoire, durée courte, approbation, audit renforcé, révocation automatique) | **`emergency_access_grant`** — motif ≥ 10 caractères (CHECK), durée 1-8 h (CHECK), **jamais auto-approuvé** (CHECK + contrôle applicatif). L'octroi réel réutilise **intégralement** `UserAssignmentService.GrantAsync` (Phase 22) : le rôle demandé est accordé avec `valid_until` = expiration automatique, héritant gratuitement de la garde anti-élévation de privilège existante (l'approbateur ne peut accorder que ce qu'il détient lui-même). Révocation anticipée → `RevokeAsync` sur l'affectation sous-jacente, effet immédiat (vérifié par test : la permission disparaît du profil de l'utilisateur) |
| 13-14 | Permissions financières/comptables (voir/créer/modifier/approuver/payer/rembourser/annuler/clôturer/exporter/administrer) | **Hérité, granularité légèrement plus grossière que le modèle cible** — `Permissions.Finance`/`Accounting` couvrent l'essentiel (View/Create/Post≈Approve/Reverse≈Cancel/ManageChart≈Administer/Export) ; l'« approbation » elle-même est déléguée au workflow générique par type d'opération plutôt qu'incarnée par une permission dédiée par domaine. Retravailler la matrice complète — **backlog**, cosmétique plus que fonctionnel (l'approbation est déjà gouvernée) |
| 15-16 | Scope multi-entreprises (comptable simple vs comptable en chef vs rôle groupe) ; isolation tenants (contrôle à chaque requête, tests de non-fuite, exports scoped) | **Hérité et validé** (Phase 3/9/22) — non retouché |
| 17-20 | Chiffrement (TLS/repos/clés séparées/rotation/secrets centralisés) ; gestion des secrets ; clés cryptographiques ; signatures Ed25519 | TLS/HSTS — hérité (Phase 3). **Nouveauté** : premier chiffrement applicatif réel d'un secret au repos (MFA, AES-256-GCM, clé de configuration avec repli développement nommé — même convention que `Jwt:SigningKey`). **Pas de coffre-fort centralisé** — chaque phase garde sa convention (`api_key` = hash seul, `integration_connector.webhook_secret` = clair par nécessité HMAC, `credentials_ref` = pointeur vers un coffre externe non implémenté) ; **honnêteté du constat inscrite dans le registre de conformité** (`encryption-secrets` = `partial`). Ed25519/signatures applicatives — **backlog**, la signature électronique GED (Phase 45) délègue déjà à un provider externe |
| 21-22 | Sécurité API (auth/scopes/rate limit/validation/CORS/SSRF/pagination/timeout/audit) ; sécurité Web (XSS/CSRF/injection/clickjacking/headers/cookies/CSP) | **Hérité** (Phase 3 rate limiter, CORS, en-têtes `X-Content-Type-Options`/`X-Frame-Options`/`Referrer-Policy`/`Permissions-Policy`). **Nouveauté** : en-tête **Content-Security-Policy** ajouté (`default-src 'none'`, posture la plus stricte pour une API JSON pure). **Protection SSRF sur les webhooks sortants toujours absente** (seule la validation de schéma http/https existe, Phase 12/46) — écart de sécurité explicite, inscrit dans le registre de conformité (`api-ssrf` = `not_met`) plutôt que passé sous silence |
| 23-25 | Sécurité base de données (comptes séparés, moindre privilège, migrations contrôlées) ; sécurité fichiers (antivirus/MIME/quotas) ; sécurité mobile/offline | **Hérité** (Phase 3 rôle `nexora_app` sans BYPASSRLS, Phase 45 limites de taille/type déclarées sans inspection réelle du contenu — déjà documenté comme backlog en Phase 45, non retravaillé ici) |
| 26-28 | Journalisation (auth/élévation/CRUD/paiement/export/permissions) ; audit log (qui/quoi/quand/scope/avant/après/résultat/correlation id) ; monitoring sécurité (connexions anormales/brute force/abus API/échecs MFA/exports inhabituels) | **Hérité et confirmé généralisé** — `audit_log` (Phase 11) capture déjà la quasi-totalité des tables via trigger générique, interrogeable sans wrapper dédié (`IAuditQueryService` générique, contrairement à l'hypothèse initiale d'une couche par fonctionnalité). Monitoring d'anomalies d'authentification (connexions impossibles, création de comptes en masse) — **backlog**, le moteur anti-fraude existant (Phase 11) reste scopé POS/stock/finance, pas authentification |
| 29-31 | Détection incidents (signal/corrélation/priorité/escalade/containment/post-mortem) ; réponse incident ; violations de données (classification/confinement/notification) | **`continuity_incident` étendu** — colonne `domain` (`availability`/`security`) + 6 catégories dédiées (breach/unauthorized_access/credential_leak/data_exfiltration/malware/phishing), validées selon le domaine. **Jamais un second registre** : sévérité/runbook (9 étapes)/post-mortem obligatoire (cause/impact/actions/prévention/responsable)/audit sont ceux de la Phase 36, inchangés. Alias `GET/POST /api/v1/security/incidents` sur `ContinuityController` existant. Violation de données — pas de workflow de notification légale dédié (dépend de juridictions non modélisées), **backlog explicite** |
| 32-34 | Classification données (public/interne/confidentiel/très sensible…) ; minimisation ; vie privée (finalité/accès/rectification/suppression/consentement) | **Backlog explicite, honnêtement inscrit au registre de conformité** (`privacy-classification`/`privacy-consent` = `not_met`) — seul un tag ponctuel existe sur les exports (`export_log.sensitivity`, Phase 7), aucune taxonomie générale ni flux de consentement/droits des personnes |
| 35 | Conformité (registre de politiques, contrôles, preuves, révisions, responsables) | **`compliance_control`** — 15 contrôles amorcés (iam/encryption/audit/privacy/backup_dr/api_security/payment/vendor), statuts **honnêtes** (`met`/`partial`/`not_met`/`not_applicable`), révisables avec preuve/notes/réviseur/date. Explicitement une **auto-évaluation**, jamais une certification externe (SOC2/ISO exigent un auditeur tiers, hors périmètre applicatif) |
| 36 | PCI/paiements (pas de stockage carte, PSP conformes, environnements séparés) | **Hérité et validé** (Phase 12) — abstraction PSP, aucun champ de carte bancaire dans le schéma. Contrôle de conformité `payment-tokenization` = `met` |
| 37-38 | Sauvegardes (chiffrement/rétention/tests) ; disaster recovery (RPO/RTO/failover/exercices) | **Hérité et validé** (Phase 36) — non retouché |
| 39-41 | Sécurité locale/cloud/hybride (pare-feu, IAM cloud, WAF, agents chiffrés) | **Hors périmètre applicatif** — relève de l'infrastructure de déploiement, non modélisable dans le code métier |
| 42-44 | Vulnerability management (inventaire/scan/SLA) ; SAST/DAST/SCA ; penetration testing | **`security_finding`** — registre unifié couvrant les 3 sections : `source` (sast/dast/sca/dependency/pentest/manual), SLA calculé automatiquement par sévérité à la déclaration (critique=7j, élevée=30j, moyenne=90j, faible=180j, info=365j), cycle `open→triaged→remediating→resolved/accepted_risk/false_positive`. **Le registre enregistre des constats, il ne scanne rien lui-même** — aucun outil SAST/DAST/SCA n'est exécuté par NEXORA ONE ; alimentation manuelle ou par import externe (non construit), honnêtement backlog |
| 45 | Sécurité supply chain (dépendances verrouillées, SBOM, signatures, CI sécurisée) | Contrôle de conformité `vendor-supply-chain` = `partial` (versions figées via csproj/lockfiles, aucun SBOM ni vérification de signature) — **backlog**, hors périmètre applicatif (outillage CI/CD) |
| 46-47 | Environnements (dev/test/staging/prod séparés) ; accès production (MFA/justification/JIT-JEA/durée limitée/pas de compte partagé) | Séparation d'environnement — infrastructure (hors périmètre). **JIT/JEA** — **déjà livré en Phase 22** (`user_role.valid_from/valid_until/reason`), confirmé et **réutilisé** comme fondation du break-glass de cette phase plutôt que reconstruit |
| 48-49 | Administration plateforme (rôles séparés, double contrôle) ; support client (accès temporaire consenti, scope, durée, audit, révocation) | Administration plateforme — **hérité** (Phase 38, 7 rôles plateforme dédiés). Support client — **`support_access_grant` (Phase 38) réexaminé** : le cycle demande/consentement/approbation/expiration/révocation est complet et audité, **mais son `ApproveAsync` ne fait qu'horodater une ligne** — aucun code ne vérifie aujourd'hui qu'un accès staff aux données d'un client repose sur une ligne active de cette table. **Écart identifié et documenté explicitement**, pas corrigé cette phase (nécessiterait de retracer le chemin réel d'accès du personnel support aux données client, hors périmètre resserré de cette phase) |
| 50-52 | Sécurité marketplace (review/scopes/sandbox/kill switch) ; sécurité webhooks (signature/replay/rotation) ; sécurité email/SMS (anti-abus/liens temporaires/phishing) | **Hérité et validé** (Phase 46 marketplace review + scopes bornés ; Phase 12 signature HMAC webhooks ; Phase 34 templates/rate-limit notifications) — non retouché |
| 53 | Gestion des certificats (expiration/renouvellement/alertes/rotation/inventaire/audit) | **`security_certificate`** — classification automatique par échéance (`active`/`expiring_soon` ≤30j/`expired`), rotation tracée (`rotated_to`, lignée jamais perdue), job planifié **`security.check_certificates`** (recalcule le statut de tous les certificats, ajouté au planificateur existant plutôt qu'un second) |
| 54-55 | Sécurité des exports (permission/scope/watermark/expiration/audit) ; sécurité rapports (RLS/masquage/audit) | **Hérité et validé** (Phase 7/31 `export_log`, RLS générique) — non retouché |
| 56-57 | Security score (couverture MFA/vulnérabilités/patch/secrets/backups/audit/incidents/permissions, explicable) ; security dashboard | **`ISecurityScoreService`** — calculé à la lecture (jamais un nouveau stockage), 6 facteurs explicités (couverture MFA, vulnérabilités ouvertes, certificats à échéance, incidents de sécurité, signaux anti-fraude, accès d'urgence en attente), note A-E. Tableau de bord agrège en plus sessions actives/appareils de confiance/approbations en attente/couverture MFA brute |
| 58 | API principales | Toutes les routes servies — voir § Backend/API. Correspondance quasi exacte avec la liste du cahier des charges (`/security/sessions/revoke` reste sous forme `{id}/revoke`, plus RESTful, documenté ci-dessous) |
| 59 | Tests | `SecurityGovernanceTests` (8) + `SecurityGovernanceEndpointsTests` (4) — cf. ci-dessous |
| 60-61 | Scénarios ; critères de sortie | IAM/RBAC/MFA/sessions/permissions/multi-entreprises/isolation/chiffrement (partiel)/API-Web-DB-fichiers security/audit/backup/DR : **hérités et validés**. Break-glass/incidents sécurité/vulnérabilités/certificats/conformité/score/dashboard/appareils/CSP : **livrés**. WebAuthn/SSO/RBAC département/SSRF webhooks/classification données/consentement/SAST-DAST réels/support access enforcement : **backlog explicite**, détaillé ci-dessous |
| 62 | Étape suivante — Phase 48 (Résilience, HA, DR) | hors périmètre |

## `V060` — contenu (4 tables neuves, 3 colonnes, 2 permissions)

- **`security_finding`** (NEW, **sans RLS entreprise** — table plateforme comme `platform_plan`) —
  registre de vulnérabilités/résultats SAST-DAST-SCA-pentest, SLA calculé par sévérité à la
  déclaration, cycle de statut à 6 valeurs.
- **`security_certificate`** (NEW, sans RLS) — inventaire de certificats, classification
  automatique par échéance, rotation avec lignée (`rotated_to`, auto-référence).
- **`compliance_control`** (NEW, sans RLS) — registre de conformité auto-évalué, 15 contrôles
  amorcés reflétant l'état réel du code (y compris les manques).
- **`emergency_access_grant`** (NEW, RLS entreprise) — demande/décision de break-glass ; motif
  ≥ 10 caractères et durée 1-8h en CHECK ; `emergency_access_grant_self_decision_chk` interdit
  l'auto-approbation **au niveau base de données**, pas seulement applicatif ; `underlying_
  assignment_id` trace la ligne `user_role` réellement créée par `GrantAsync` (Phase 22).
- **`continuity_incident`** (Phase 36, ALTER) — `+ domain` (`availability`/`security`,
  défaut `availability` pour les lignes existantes) ; catégorie étendue à 6 valeurs de sécurité.
- **`user_session`** (Phase 11, ALTER) — `+ device_type` (inféré du user-agent) `+ trusted` (bool).
- `trg_audit_*` sur les 4 nouvelles tables. Permissions **`security.request_emergency_access`**
  (large : super_administrator/group_administrator/owner_direction/manager) et
  **`security.approve_emergency_access`** (restreinte : super_administrator/group_administrator/
  owner_direction). Job planifié **`security.check_certificates`**.
- **249 tables (+4), 210 RLS (+1)**, 60 migrations (V001–V060).

## Backend

| Couche | Fichier |
|---|---|
| Contracts | `Nexora.Contracts/Security/SecurityContracts.cs` (étendu : findings/certificats/conformité/break-glass/score/dashboard/appareil) ; `Nexora.Contracts/Continuity/ContinuityContracts.cs` (étendu : `Domain`) |
| Application | `Nexora.Application/Security/Abstractions.cs` (étendu : `IMfaSecretProtector`, `ISecurityFindingService`, `ISecurityCertificateService`, `IComplianceRegistryService`, `IEmergencyAccessService`, `ISecurityScoreService`, `ISessionSecurityService.SetTrustedAsync`) ; `PermissionCatalog.Security.{RequestEmergencyAccess,ApproveEmergencyAccess}` |
| Infrastructure | `Security/SecurityGovernance.cs` (5 services) ; `Identity/MfaSecretProtector.cs` (AES-256-GCM) ; `Identity/MfaService.cs` (étendu : chiffrement transparent) ; `Security/ApprovalAndSession.cs` (étendu : appareils) ; `Continuity/SqlContinuityService.cs` (étendu : domaine) ; `Scheduling/SchedulerRunner.cs` (+1 job) ; `Api/Middleware/SecurityHeadersMiddleware.cs` (+CSP) |
| API | `Nexora.Api/Controllers/SecurityController.cs` (étendu : 15 routes) ; `AuthMfaController.cs` (alias `/security/mfa*`) ; `ContinuityController.cs` (alias `/security/incidents`) |

### Points de conception

- **Le break-glass réutilise le moteur JIT existant, jamais un second système d'octroi (§12, §47)**
  — `SqlEmergencyAccessService.DecideAsync` appelle directement `UserAssignmentService.GrantAsync`
  (Phase 22) pour l'octroi réel : le rôle demandé devient une ligne `user_role` ordinaire avec
  `valid_until` = expiration automatique. Cela hérite **gratuitement** de la garde anti-élévation de
  privilège déjà testée (l'approbateur ne peut accorder que ce qu'il détient lui-même) — aucune
  logique de permission dupliquée.
- **Auto-approbation interdite à deux niveaux, pas un seul (§6)** — un CHECK en base
  (`emergency_access_grant_self_decision_chk`) **et** un contrôle applicatif explicite
  (`emergency_access.self_decision`) empêchent le même utilisateur de décider de sa propre demande,
  même s'il détient par ailleurs la permission d'approbation — défense en profondeur, vérifiée par
  test HTTP bout en bout.
- **Un second domaine sur le même registre d'incidents, jamais un registre parallèle (§29-30)** —
  `continuity_incident` gagne une colonne `domain` plutôt qu'une nouvelle table
  `security_incident` : sévérité/runbook/post-mortem/audit restent ceux de la Phase 36, la seule
  vraie nouveauté est un jeu de catégories dédié aux incidents de sécurité, validé selon le domaine.
- **Un appareil est une session, jamais un second registre (§11)** — `device_type` (inféré du
  user-agent à l'enregistrement) et `trusted` sont deux colonnes ajoutées à `user_session` ;
  `GET/POST /security/devices*` sont des alias directs sur `ISessionSecurityService`.
- **Chiffrement du secret MFA transparent au service, encapsulé dans un seul point d'entrée-sortie**
  — `SqlMfaService.LoadUserAsync` déchiffre à la lecture, les deux points d'écriture chiffrent ;
  aucun autre appelant ne lit jamais `mfa_secret` directement. Convention de clé reproduite depuis
  `Jwt:SigningKey` (`Security:MfaEncryptionKey`, repli développement explicitement nommé).
- **Le score de sécurité est calculé à la lecture, jamais stocké (§56-57)** — `ISecurityScoreService`
  interroge directement MFA/sessions/fraude/approbations/incidents/vulnérabilités/certificats à
  chaque appel ; aucune table de snapshot, cohérent avec le principe déjà établi (ex. `IntegrationCenter`
  Phase 12) que les tableaux de bord transverses ne sont pas une source de vérité supplémentaire.
- **Registre de conformité honnête, pas un vernis** — les 15 contrôles amorcés incluent
  délibérément des statuts `not_met` (classification des données, protection SSRF, consentement) :
  le but est de rendre visible ce qui manque, pas de prétendre une conformité inexistante.
- **`support_access_grant` (Phase 38) examiné, écart documenté plutôt que corrigé à la hâte** — son
  cycle demande/consentement/expiration est complet et audité, mais rien ne vérifie qu'un accès
  staff réel aux données d'un client repose sur une ligne active de cette table. Le corriger
  exigerait de retracer le chemin d'accès du personnel support aux données client — jugé hors du
  périmètre resserré de cette phase plutôt que bâclé.
- **Bug de service découvert et corrigé pendant l'implémentation** — `AesGcmMfaSecretProtector`
  exigeait `IConfiguration` en injection, absent du conteneur de test léger
  (`FinanceEngineFixture`, un `ServiceCollection` nu sans hôte ASP.NET Core) ; corrigé en
  enregistrant explicitement `IConfiguration` dans la fixture — bénéficie à tout futur service
  Infrastructure ayant besoin de configuration.

## Tests d'intégration — `SecurityGovernanceTests` (8, `[Collection("finance")]`, entreprise c2)

| Test | § | Vérifie |
|---|---|---|
| `Emergency_access_requires_a_third_party_and_grants_a_real_time_boxed_role` | 6, 12 | auto-décision refusée ; un tiers approuve ; l'octroi est réel (permission `catalog.view` apparaît) ; révoquer retire l'accès immédiatement |
| `Emergency_access_validates_reason_length_and_duration_bounds` | 12 | motif < 10 caractères refusé ; durée hors 1-8h refusée |
| `Emergency_access_rejection_grants_nothing_and_cannot_be_replayed` | 6 | un refus n'accorde rien ; une décision déjà tranchée ne peut être rejouée |
| `Security_findings_assign_sla_by_severity_and_track_resolution` | 42-44 | SLA calculé par sévérité (critique=7j, faible=180j) ; cycle de résolution |
| `Certificates_are_classified_by_expiry_and_rotation_preserves_lineage` | 53 | classification automatique (actif/bientôt expiré/expiré) ; rotation trace la lignée |
| `Compliance_controls_are_seeded_honestly_and_can_be_reviewed_with_evidence` | 35 | contrôles amorcés reflètent l'état réel (MFA = partiel) ; révision avec preuve |
| `Continuity_incidents_extend_to_a_security_domain_with_dedicated_categories` | 29-30 | catégorie invalide pour le domaine refusée ; filtrage par domaine sans registre parallèle |
| `Security_dashboard_reflects_new_findings_certificates_and_incidents` | 56-57 | le tableau de bord reflète en temps réel de nouveaux constats/certificats/incidents |

## Tests API — `SecurityGovernanceEndpointsTests` (4)

| Test | Vérifie |
|---|---|
| `Security_governance_routes_require_authentication` | 8 routes sans jeton → 401 |
| `Read_only_security_governance_endpoints_are_wired_over_http` | 6 endpoints de lecture → 200, y compris les alias `/security/mfa` et `/security/incidents` |
| `Platform_only_security_registries_are_reserved_to_platform_staff` | vulnérabilités/certificats/conformité → 403 pour un utilisateur entreprise sans `platform.security` |
| `Emergency_access_self_decision_is_rejected_over_http` | même un utilisateur détenant la permission d'approbation ne peut décider de sa propre demande |

## Frontend

`src/security/securityApi.ts` étendu (types + fonctions : appareils/confiance, break-glass,
vulnérabilités, certificats, conformité, score/dashboard) + **`SecurityPage.tsx`** (page existante,
Phase 11, étendue plutôt que dupliquée) — 3 nouveaux onglets :

- **Vue d'ensemble** — score explicable (6 facteurs) + compteurs transverses ;
- **Accès d'urgence** — demander/approuver/refuser/révoquer, motif et durée visibles ;
- **Vulnérabilités & conformité** — constats avec SLA, certificats avec échéance, contrôles de
  conformité avec statut coloré (met/partial/not_met) ;
- Onglet **Sessions** enrichi : type d'appareil, bascule de confiance.

Vitest `securityGovernanceApi.test.ts` (7) → **103 tests frontend**.

## Manques assumés (backlog)

- WebAuthn/Passkey (§7-8) — aucune dépendance de cérémonie WebAuthn ajoutée cette phase
- SSO (§7) — hors périmètre, nécessiterait un fournisseur d'identité externe
- MFA obligatoire pour rôles sensibles reste **consultative**, jamais bloquante côté serveur (§8) —
  un blocage réel aurait cassé l'accès de tous les comptes de démonstration existants (Direction,
  chief_accountant…) qui n'ont pas de MFA enrôlée ; décision consciente de ne pas casser
  rétroactivement l'accès plutôt qu'un correctif risqué en fin de phase
- RBAC niveau département (§4) — absent du schéma, changement structurel hors périmètre
- Permissions financières/comptables : matrice complète Voir/Créer/Modifier/Approuver/Payer/
  Rembourser/Annuler/Clôturer/Exporter/Administrer (§13-14) — l'approbation est déjà gouvernée par
  le workflow générique, la granularité par permission reste cosmétique
- Protection SSRF sur les URLs de webhooks sortants (§21-22, §51) — écart hérité de la Phase 46,
  toujours non corrigé, inscrit explicitement au registre de conformité (`api-ssrf` = `not_met`)
- Classification des données et consentement/droits des personnes (§32-34) — aucune taxonomie
  générale, aucun flux d'accès/rectification/suppression
- Notification légale de violation de données (§31) — dépend de juridictions non modélisées
- SAST/DAST/SCA/pentest réels (§43-44) — `security_finding` enregistre des constats, n'exécute
  aucun outil ; alimentation manuelle ou import externe non construit
- SBOM et vérification de signature des dépendances (§45)
- `support_access_grant` (Phase 38) non branché sur un chemin réel d'accès aux données client
  (§49) — écart identifié et documenté explicitement, correction hors périmètre de cette phase
- Consolidation des vérifications d'auto-approbation ad hoc (`continuity`/`data_correction`) dans
  le workflow générique `IApprovalWorkflowService` — cohérentes mais dupliquées, nettoyage possible
  d'une phase future
