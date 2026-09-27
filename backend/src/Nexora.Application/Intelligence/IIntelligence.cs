using Nexora.Contracts.Intelligence;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;

namespace Nexora.Application.Intelligence;

/// <summary>
/// Recommandations (Phase 43 §2, §4-5, §12-13, §18, §20-21, §31, §33, §46) — la couche manquante
/// entre « on a détecté » (Phase 42 <see cref="Reporting.IInsightService"/>) et « voici quoi faire ».
/// Chaque règle est un contrôle SQL borné, comme les insights (§37 Phase 42) ; jamais d'action
/// financière automatique (§32) — accepter/reporter/ignorer restent des décisions humaines.
/// </summary>
public interface IRecommendationService
{
    Task<IReadOnlyList<RecommendationDto>> ListAsync(string? status, string? category, CancellationToken ct = default);
    Task<Result<RecommendationDto>> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Résumé fonctionnel §17 : passe « nouvelle » → « vue » à la première consultation.</summary>
    Task<Result<RecommendationDto>> MarkViewedAsync(Guid id, CancellationToken ct = default);

    /// <summary>§31 « accepter » — <c>CreateTask</c> crée en plus une tâche assignée (comble le manque §31).</summary>
    Task<Result<RecommendationDto>> AcceptAsync(Guid id, AcceptRecommendationRequest request, CancellationToken ct = default);
    Task<Result> DismissAsync(Guid id, DismissRecommendationRequest request, CancellationToken ct = default);
    Task<Result<RecommendationDto>> SnoozeAsync(Guid id, SnoozeRecommendationRequest request, CancellationToken ct = default);

    /// <summary>Boucle d'apprentissage (§33) — résultat positif/négatif + motif facultatif, historisé.</summary>
    Task<Result<RecommendationDto>> ResolveAsync(Guid id, ResolveRecommendationRequest request, CancellationToken ct = default);

    /// <summary>Phase 54 §16, §19 « modifier » — ajuste une valeur proposée avant validation, sans
    /// écraser la proposition d'origine (§18 : valeur initiale ET valeur modifiée restent visibles).</summary>
    Task<Result<RecommendationDto>> ModifyAsync(Guid id, ModifyRecommendationRequest request, CancellationToken ct = default);

    /// <summary>Applique les règles activées pour l'entreprise — appelé par le planificateur
    /// (job <c>intelligence.generate_recommendations</c>). Expire d'abord les recommandations
    /// dont <c>expires_at</c> est dépassé (Phase 54 §6, §21) avant d'en générer de nouvelles.</summary>
    Task<int> GenerateAsync(CancellationToken ct = default);

    /// <summary>Vue générale (Phase 54 §5, §20) — comptes par niveau de priorité + actions
    /// prioritaires + opportunités, agrégés depuis les recommandations existantes.</summary>
    Task<IntelligenceDashboardDto> DashboardAsync(CancellationToken ct = default);
}

/// <summary>Règles configurables (§34, §39) — active <c>intelligence_rule</c>. Les valeurs système
/// (<c>company_id</c> NULL, comme <c>sector_profile</c>) ne sont jamais créées via l'API ; une
/// entreprise ne fait que surcharger (upsert sur sa propre clé).</summary>
public interface IIntelligenceRuleService
{
    Task<IReadOnlyList<IntelligenceRuleDto>> ListAsync(CancellationToken ct = default);
    Task<Result<IntelligenceRuleDto>> SaveAsync(SaveIntelligenceRuleRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Scoring explicable (§35) — <c>entity_score</c>, recalculé par le planificateur, jamais
/// une boîte noire : chaque score porte les variables utilisées (<c>explanation</c>).</summary>
public interface IScoringService
{
    Task<IReadOnlyList<EntityScoreDto>> ListAsync(string? entityType, CancellationToken ct = default);

    /// <summary>Recalcule les scores — appelé par le planificateur (job <c>intelligence.generate_recommendations</c>).</summary>
    Task<int> ComputeAsync(CancellationToken ct = default);
}

/// <summary>Assistants par rôle (§6-10) & assistant conversationnel (§28-30). Les briefings par
/// rôle réutilisent les tableaux de bord existants (Phase 7 <see cref="Reporting.IKpiService"/>) —
/// aucune redérivation — et les enrichissent des insights/recommandations pertinents pour le rôle.
/// <see cref="QueryAsync"/> délègue à <see cref="Reporting.IAskNexora"/> (Phase 7, déterministe, sans
/// LLM) et journalise la question pour l'observabilité (§38).</summary>
public interface IAssistantService
{
    /// <param name="role">direction | manager | comptable | finance | caissier (§6-10).</param>
    /// <param name="sessionId">requis pour <c>caissier</c> ; ignoré sinon. Sans session ouverte,
    /// le tableau de bord est vide et l'en-tête l'indique — jamais un chiffre inventé (§2).</param>
    Task<AssistantBriefingDto> BriefingAsync(string role, Guid? sessionId, CancellationToken ct = default);

    Task<AskResponse> QueryAsync(AskRequest request, CancellationToken ct = default);
}

/// <summary>Tâche générique (§31) — comble un manque réel : <c>group_task</c> (V028) exige un
/// groupe, <c>onboarding_task</c> (V052) est une checklist fixe. Liée le cas échéant à une
/// recommandation/un insight via <c>reference_type</c>/<c>reference_id</c> (comme <c>notification</c>).</summary>
public interface ITaskService
{
    Task<IReadOnlyList<TaskDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<Result<TaskDto>> CreateAsync(CreateTaskRequest request, CancellationToken ct = default);
    Task<Result<TaskDto>> CompleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Observabilité IA (§38) — journal d'audit filtré aux tables de ce module. Les tables
/// concernées sont auditées par <c>trg_audit_*</c> (V056) ; ce service ne fait que lire
/// <c>audit_log</c> (immuable, Phase 11) avec ce filtre.</summary>
public interface IIntelligenceAuditQuery
{
    Task<IReadOnlyList<IntelligenceAuditEntryDto>> RecentAsync(int limit, CancellationToken ct = default);
}

/// <summary>
/// Catalogue descriptif des générateurs déjà branchés — règles de recommandation + scoreurs
/// (gouvernance §39 : propriétaire, version, périmètre, critères). Statique, comme
/// <see cref="Reporting.KpiCatalog"/> (Phase 7) : les seuils réels vivent dans <c>intelligence_rule</c>,
/// ceci documente seulement ce qui existe.
/// </summary>
public static class IntelligenceModelCatalog
{
    public static IReadOnlyList<IntelligenceModelDto> All { get; } =
    [
        new("stock_reorder", "rule", "stock", "Réapprovisionnement intelligent",
            "Vélocité de vente + stock actuel + délai fournisseur + stock de sécurité → quantité suggérée (§13).",
            "NEXORA ONE", 1, ["sale_line", "stock_balance", "product_reorder_rule"]),
        new("stock_dormant", "rule", "stock", "Produit dormant / surstock",
            "Aucune vente récente + marge faible → réduire le réapprovisionnement et réévaluer la promotion (§51).",
            "NEXORA ONE", 1, ["stock_movement", "sale_line"]),
        new("receivable_priority", "rule", "receivables", "Priorisation du recouvrement",
            "Ancienneté × montant dû → relance suggérée, sans décision de crédit automatique (§18).",
            "NEXORA ONE", 1, ["customer_receivable"]),
        new("cashflow_risk", "rule", "treasury", "Risque de trésorerie",
            "Délègue à la prévision de trésorerie (Phase 28/42) — alerte si le solde projeté passe sous le seuil (§20).",
            "NEXORA ONE", 1, ["financial_account", "customer_receivable", "supplier_payable"]),
        new("budget_overrun", "rule", "budget", "Dépassement budgétaire probable",
            "Projection du réalisé au prorata de la période écoulée vs ligne budgétaire (§21).",
            "NEXORA ONE", 1, ["budget_line", "journal_entry"]),
        new("stock_coverage", "score", "stock", "Score de couverture de stock",
            "Jours de couverture = stock actuel / vélocité de vente quotidienne (§35).",
            "NEXORA ONE", 1, ["stock_balance", "sale_line"]),
        new("credit_risk", "score", "customer", "Score de risque créance",
            "Combine ancienneté, montant, litiges ouverts et blocage (§35).",
            "NEXORA ONE", 1, ["customer_receivable", "customer_dispute", "customer_block"]),
        new("supplier_reliability", "score", "supplier", "Score de fiabilité fournisseur",
            "Taux de réception dans les délais + taux de factures sans écart de rapprochement (§35).",
            "NEXORA ONE", 1, ["purchase_order", "goods_receipt", "supplier_invoice"]),
        new("sales_spike", "rule", "sales", "Hausse inhabituelle des ventes",
            "Compare le chiffre d'affaires moyen quotidien récent à la période précédente équivalente (§9.2, Phase 54).",
            "NEXORA ONE", 1, ["sale"]),
        new("sales_decline", "rule", "sales", "Baisse inhabituelle des ventes",
            "Même comparaison que la hausse, seuil de baisse distinct et configurable (§9.2, Phase 54).",
            "NEXORA ONE", 1, ["sale"]),
        new("customer_inactive", "rule", "customers", "Client devenu inactif",
            "Client avec un historique d'achats significatif, sans achat récent au-delà du seuil configuré (§9.4, Phase 54).",
            "NEXORA ONE", 1, ["customer", "sale"]),
        new("supplier_price_increase", "rule", "suppliers", "Hausse des prix fournisseur",
            "Compare le prix d'achat moyen récent au prix moyen antérieur, par produit et par fournisseur (§9.5, Phase 54).",
            "NEXORA ONE", 1, ["purchase_order", "purchase_order_line"]),
    ];
}
