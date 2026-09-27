using Nexora.Contracts.Continuity;
using Nexora.Domain.Common;

namespace Nexora.Application.Continuity;

/// <summary>
/// Registre applicatif de continuité (Phase 36). La sauvegarde physique et le failover sont des
/// opérations d'infrastructure ; ce service en tient le **catalogue**, les **vérifie**, pilote les
/// **tests de restauration** (§21) et le **runbook d'incident** (§43-45), et calcule l'**état de
/// reprise** (RPO/RTO observés vs objectifs, §36). Une restauration en production exige une
/// validation par un tiers (§39).
/// </summary>
public interface IContinuityService
{
    Task<BackupPolicyDto> GetPolicyAsync(CancellationToken ct = default);
    Task<Result<BackupPolicyDto>> SetPolicyAsync(BackupPolicyDto policy, CancellationToken ct = default);

    Task<IReadOnlyList<BackupDto>> ListBackupsAsync(CancellationToken ct = default);
    Task<Result<BackupDto>> RegisterBackupAsync(RegisterBackupRequest request, CancellationToken ct = default);
    Task<Result<BackupVerificationDto>> VerifyBackupAsync(Guid backupId, CancellationToken ct = default);

    Task<IReadOnlyList<RestoreRunDto>> ListRestoreRunsAsync(CancellationToken ct = default);
    Task<Result<RestoreRunDto>> RunRestoreTestAsync(RestoreTestRequest request, CancellationToken ct = default);
    /// <summary>Crée une demande de restauration en production (en attente d'approbation §39).</summary>
    Task<Result<RestoreRunDto>> RequestProductionRestoreAsync(RestoreRequest request, CancellationToken ct = default);
    /// <summary>Approuve puis marque exécutée une restauration en production (approbateur ≠ demandeur).</summary>
    Task<Result<RestoreRunDto>> ApproveProductionRestoreAsync(Guid restoreRunId, CancellationToken ct = default);

    Task<DisasterRecoveryStatusDto> DisasterRecoveryStatusAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ContinuityIncidentDto>> ListIncidentsAsync(string? status, CancellationToken ct = default);
    /// <summary>Phase 47 §29-30 : filtre optionnel par domaine (availability/security) — même registre.</summary>
    Task<IReadOnlyList<ContinuityIncidentDto>> ListIncidentsAsync(string? status, string? domain, CancellationToken ct = default);
    Task<Result<ContinuityIncidentDto>> OpenIncidentAsync(OpenIncidentRequest request, CancellationToken ct = default);
    Task<Result<ContinuityIncidentDto>> AdvanceIncidentAsync(Guid incidentId, AdvanceIncidentRequest request, CancellationToken ct = default);
    Task<Result<ContinuityIncidentDto>> ResolveIncidentAsync(Guid incidentId, ResolveIncidentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Contrôle de santé exécuté par le planificateur (§36-37) : sauvegarde en retard vs la
    /// fréquence de la politique, ou dernier test de restauration en échec → notification aux
    /// administrateurs. Renvoie le nombre d'alertes émises.
    /// </summary>
    Task<int> RunHealthChecksAsync(CancellationToken ct = default);

    /// <summary>
    /// Phase 48 §32, §55 — verrouillage d'urgence : révoque toutes les sessions et clés d'API de
    /// l'entreprise (containment réel, pas une intention documentée). Réservé à `continuity.restore`
    /// (même niveau de confiance qu'une restauration production) ; journalisé sur l'incident.
    /// </summary>
    Task<Result<LockdownResultDto>> LockDownAsync(Guid incidentId, CancellationToken ct = default);

    // --- §44-45 Exercices DR --------------------------------------------------------------------
    Task<IReadOnlyList<DrTestDto>> ListDrTestsAsync(string? status, CancellationToken ct = default);
    Task<Result<DrTestDto>> ScheduleDrTestAsync(ScheduleDrTestRequest request, CancellationToken ct = default);
    Task<Result<DrTestDto>> RecordDrTestResultAsync(Guid id, RecordDrTestResultRequest request, CancellationToken ct = default);

    // --- §7, §39 Page de statut — composition, jamais un second calcul --------------------------
    Task<StatusPageDto> StatusPageAsync(CancellationToken ct = default);
}

/// <summary>
/// Catalogue statique des runbooks (§37) — guidance concrète par scénario, reliant explicitement
/// les fonctionnalités déjà livrées (sauvegardes Phase 36, verrouillage §32, certificats Phase 47,
/// restauration à 2 personnes §39) plutôt que des généralités. Comme <c>IntelligenceModelCatalog</c>
/// (Phase 43) : contenu de référence, jamais configurable par entreprise.
/// </summary>
public static class ContinuityRunbookCatalog
{
    public static IReadOnlyList<RunbookDto> All { get; } =
    [
        new("db_outage", "Panne de base de données",
            "La base de données ne répond plus ou répond avec des erreurs.",
            [
                new(1, "Détecter", "Vérifier GET /api/v1/ops/diagnostics — le contrôle « database » indique fail."),
                new(2, "Qualifier", "Ouvrir un incident (POST /continuity/incidents) catégorie server_failure ou db_corruption, sévérité selon impact."),
                new(3, "Isoler", "Passer l'entreprise en mode maintenance si l'écriture risque d'aggraver l'état (Phase 41)."),
                new(4, "Restaurer", "Identifier la dernière sauvegarde vérifiée (GET /backups, statut verified) antérieure à l'incident."),
                new(5, "Valider en isolation", "POST /backups/{id}/restore-test avant toute action en production."),
                new(6, "Restaurer en production", "POST /restores puis approbation par un tiers (POST /restores/{id}/approve) — jamais le demandeur."),
                new(7, "Vérifier", "Contrôler les contraintes DB, les soldes et les compteurs (§43) avant de rouvrir le trafic."),
                new(8, "Clôturer", "POST /continuity/incidents/{id}/resolve avec un post-mortem complet."),
            ]),
        new("data_corruption", "Corruption de données",
            "Des données incohérentes sont détectées (contraintes violées, soldes impossibles, doublons inexpliqués).",
            [
                new(1, "Isoler", "Bloquer immédiatement les écritures sur l'entité affectée (permission ou mode maintenance ciblé)."),
                new(2, "Ouvrir un incident", "Catégorie db_corruption, sévérité selon l'étendue."),
                new(3, "Identifier la dernière copie saine", "Comparer les sauvegardes vérifiées disponibles à la fenêtre d'apparition du problème."),
                new(4, "Restaurer sélectivement ou complètement", "Selon l'étendue — un restore-test d'abord, toujours."),
                new(5, "Rapprocher", "Comparer les compteurs/soldes avant et après restauration (§43)."),
                new(6, "Post-mortem", "Documenter la cause pour éviter la récidive avant de clôturer l'incident."),
            ]),
        new("server_failure", "Panne serveur",
            "Le serveur applicatif ne répond plus.",
            [
                new(1, "Détecter", "Health check en échec / alerte infrastructure."),
                new(2, "Ouvrir un incident", "Catégorie server_failure."),
                new(3, "Basculer si prévu", "Bascule vers une instance/un serveur secondaire selon l'architecture (infrastructure)."),
                new(4, "Restaurer si nécessaire", "Remplacement matériel + restauration de la configuration depuis la dernière sauvegarde config."),
                new(5, "Valider", "Vérifier /health et /health/ready avant de rouvrir le trafic."),
                new(6, "Clôturer", "Post-mortem et reprise des postes."),
            ]),
        new("network_loss", "Panne réseau",
            "Le site ou une partie du réseau perd la connectivité.",
            [
                new(1, "Confirmer le périmètre", "Local uniquement (Internet coupé) ou plus large."),
                new(2, "Basculer en mode dégradé", "Ventes/opérations locales continuent hors ligne — le moteur de synchronisation (Phase 10) met en file."),
                new(3, "Ouvrir un incident", "Catégorie network_loss si la coupure dépasse quelques minutes."),
                new(4, "Reconnecter", "À la reconnexion, la synchronisation rejoue automatiquement la file (idempotente par operation_uuid)."),
                new(5, "Vérifier les conflits", "Contrôler sync_conflict pour toute opération non résolue automatiquement."),
                new(6, "Clôturer", "Confirmer la reprise complète avant de résoudre l'incident."),
            ]),
        new("storage_failure", "Panne de stockage",
            "Le stockage (disque, volume, objet) est indisponible ou dégradé.",
            [
                new(1, "Détecter", "GET /api/v1/ops/diagnostics — contrôle « storage »."),
                new(2, "Ouvrir un incident", "Catégorie disk_failure."),
                new(3, "Isoler", "Éviter toute écriture supplémentaire sur le volume affecté."),
                new(4, "Restaurer", "Depuis la sauvegarde vérifiée la plus récente, en environnement isolé d'abord."),
                new(5, "Valider l'intégrité", "Comptage d'objets, checksum, cohérence documentaire (§43)."),
                new(6, "Clôturer", "Post-mortem incluant la cause matérielle si connue."),
            ]),
        new("ransomware", "Ransomware",
            "Chiffrement ou compromission suspectée des données ou des accès.",
            [
                new(1, "Isoler immédiatement", "Couper l'accès réseau des postes suspects (hors NEXORA ONE)."),
                new(2, "Ouvrir un incident", "Catégorie ransomware, sévérité p1, domaine security."),
                new(3, "Verrouiller les accès", "POST /continuity/incidents/{id}/lockdown — révoque toutes les sessions et clés d'API de l'entreprise touchée."),
                new(4, "Préserver les preuves", "Ne pas altérer les systèmes suspects avant investigation."),
                new(5, "Identifier une copie saine", "Une sauvegarde vérifiée, antérieure à la compromission, jamais connectée en permanence si possible (§12)."),
                new(6, "Restaurer en isolation puis en production", "restore-test d'abord, restauration production à 2 personnes ensuite."),
                new(7, "Réémettre les identifiants", "Nouveaux mots de passe, ré-enrôlement MFA si nécessaire, rotation des clés d'API (Phase 35)."),
                new(8, "Post-mortem obligatoire", "Avant clôture — cause, impact, actions, prévention, responsable."),
            ]),
        new("human_error", "Erreur humaine",
            "Suppression ou modification accidentelle de données.",
            [
                new(1, "Ouvrir un incident", "Catégorie human_error."),
                new(2, "Vérifier l'audit", "GET /security/audit pour retracer précisément l'action et son auteur (jamais accusatoire, factuel)."),
                new(3, "Point-in-time recovery si nécessaire", "Restaurer à l'instant précédant l'erreur — infrastructure, coordonné avec le registre de sauvegarde."),
                new(4, "Valider", "Comparer l'état restauré à ce qui est attendu avant de rouvrir."),
                new(5, "Clôturer", "Post-mortem — souvent une amélioration de permission ou de confirmation UI en action préventive."),
            ]),
        new("cloud_provider_outage", "Fournisseur cloud indisponible",
            "Le fournisseur d'infrastructure cloud signale une panne.",
            [
                new(1, "Vérifier le statut fournisseur", "Page de statut du fournisseur."),
                new(2, "Ouvrir un incident", "Catégorie cloud_provider."),
                new(3, "Basculer si prévu", "Selon le plan de sortie fournisseur (§54) si l'offre le permet."),
                new(4, "Communiquer", "Mettre à jour la page de statut NEXORA ONE (GET /resilience/status-page) pour les utilisateurs."),
                new(5, "Clôturer", "Post-mortem incluant la dépendance fournisseur en facteur externe."),
            ]),
        new("certificate_expiry", "Certificat expiré ou proche expiration",
            "Un certificat (TLS, signature, webhook) approche ou dépasse son échéance.",
            [
                new(1, "Détecter", "GET /security/certificates — statut expiring_soon ou expired (Phase 47)."),
                new(2, "Renouveler", "POST /security/certificates/{id}/rotate avec la nouvelle échéance."),
                new(3, "Déployer", "Mettre à jour la configuration d'infrastructure concernée (hors périmètre applicatif)."),
                new(4, "Vérifier", "Confirmer que le nouveau certificat est actif avant l'expiration de l'ancien."),
            ]),
        new("payment_provider_down", "Fournisseur de paiement indisponible",
            "Le prestataire de paiement (PSP, mobile money) ne répond plus.",
            [
                new(1, "Détecter", "Échecs de callback/paiement répétés (Phase 12)."),
                new(2, "Basculer en mode dégradé", "Autoriser les paiements en espèces/manuels le temps de l'incident (mode dégradé §40)."),
                new(3, "Ouvrir un incident", "Catégorie other, préciser le fournisseur en titre."),
                new(4, "Surveiller la reprise", "Reprendre les paiements électroniques une fois le fournisseur rétabli."),
                new(5, "Rapprocher", "Vérifier les paiements en attente/ambigus au retour (Phase 12 réconciliation)."),
            ]),
        new("restore_procedure", "Procédure de restauration",
            "Marche à suivre générique pour toute restauration, quel que soit le scénario déclencheur.",
            [
                new(1, "Choisir le point de restauration", "GET /backups — privilégier un backup au statut verified."),
                new(2, "Restaurer en environnement isolé", "POST /backups/{id}/restore-test — jamais directement en production."),
                new(3, "Valider", "Vérifier checksum, comptage d'objets, cohérence du rapport de test."),
                new(4, "Demander la restauration production", "POST /restores."),
                new(5, "Faire approuver par un tiers", "POST /restores/{id}/approve — le demandeur ne peut jamais s'auto-approuver (§39)."),
                new(6, "Health check", "Vérifier /health et les contrôles de diagnostics avant de rouvrir le trafic."),
            ]),
        new("failover_procedure", "Procédure de bascule",
            "Marche à suivre générique pour une bascule vers un système secondaire.",
            [
                new(1, "Confirmer la panne", "Health check en échec confirmé, pas un faux positif transitoire."),
                new(2, "Décider automatique ou manuel", "Automatique si sûr ; manuel si risque de split-brain ou de corruption (§26-27)."),
                new(3, "Basculer", "Vers l'instance/le site secondaire — infrastructure."),
                new(4, "Valider l'état des données", "Avant de considérer la bascule réussie."),
                new(5, "Journaliser", "Ouvrir/mettre à jour l'incident correspondant."),
            ]),
        new("return_to_normal", "Retour à la normale",
            "Marche à suivre après résolution d'un incident, avant de considérer le service pleinement rétabli.",
            [
                new(1, "Health check", "Tous les contrôles de diagnostics au vert (GET /ops/diagnostics)."),
                new(2, "Synchroniser", "S'assurer que toute file en attente (offline, notifications, webhooks) est vidée."),
                new(3, "Rapprocher", "Transactions offline, paiements, stock, caisse, documents, écritures (§42)."),
                new(4, "Vérifier l'intégrité", "Comptages, soldes, contraintes DB, audit (§43)."),
                new(5, "Rouvrir le trafic normal", "Lever le mode dégradé/maintenance."),
                new(6, "Renforcer le monitoring", "Surveillance accrue pendant les heures suivant la reprise."),
                new(7, "Clôturer l'incident", "Post-mortem complet, actions préventives assignées."),
            ]),
    ];
}
