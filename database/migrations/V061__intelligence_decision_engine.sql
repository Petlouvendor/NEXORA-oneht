-- =============================================================================
-- NEXORA — V061 — Intelligence & Business Decision Engine (Phase 54)
--
-- Le moteur de recommandations existe déjà et reste VALIDÉ, pas reconstruit (Phase 43, V056) :
-- `recommendation` (expliquée : pourquoi/données/impact/action/résultat attendu, `priority_score`
-- généré, cycle new→accepted|dismissed|snoozed→resolved avec boucle d'apprentissage `outcome`),
-- `intelligence_rule` (seuils configurables par entreprise, gabarit système), `entity_score`
-- (scoring explicable), `assistant_query` (assistant conversationnel déterministe, sans LLM),
-- `task` (action assignable). Le cahier des charges de cette phase redemande presque mot pour mot
-- ce que Phase 43 a déjà livré (RulesEngine/AnalyticsEngine/ExplanationEngine/boucle
-- d'apprentissage/séparation logique-IA/sécurité multi-entreprise) — validé, non dupliqué.
--
-- Ce qui manquait réellement, et que Phase 43 documentait déjà explicitement en backlog : les
-- règles ventes/clients/fournisseurs (§9.2, §9.4, §9.5 du nouveau cahier des charges — Phase 43
-- ne couvrait que stock/créances/trésorerie/budget), un niveau de priorité CATÉGORIEL explicite
-- (CRITIQUE/IMPORTANT/À SURVEILLER/OPPORTUNITÉ, §7) au-delà du score numérique existant, une
-- action « modifier » avant acceptation (§16, §19), une expiration effective des recommandations
-- (§6, §21 — la colonne n'existait pas), et un tableau de bord dédié agrégeant par priorité (§5).
-- =============================================================================
SET search_path TO nexora, public;

-- ===================== 1. Priorité catégorielle explicite (§7) ===============
-- `nature` distingue un risque (à traiter) d'une opportunité (à saisir) — une opportunité n'est
-- jamais « plus urgente qu'une rupture », c'est un autre axe. `priority_level` reste une colonne
-- GÉNÉRÉE, comme `priority_score` (V056) : jamais recalculée en C#, toujours cohérente avec les
-- facteurs qu'elle résume (§46 « rester explicable »).
ALTER TABLE nexora.recommendation ADD COLUMN nature text NOT NULL DEFAULT 'risk';
ALTER TABLE nexora.recommendation ADD CONSTRAINT recommendation_nature_chk CHECK (nature IN ('risk', 'opportunity'));

-- Postgres interdit qu'une colonne générée en référence une autre (`priority_score` l'est déjà,
-- V056) : la même formule est donc réécrite ici plutôt que référencée, les deux restent
-- mathématiquement identiques et sont testées comme telles.
ALTER TABLE nexora.recommendation ADD COLUMN priority_level text GENERATED ALWAYS AS (
    CASE
        WHEN nature = 'opportunity' THEN 'opportunity'
        WHEN (impact_score * urgency_score * GREATEST(confidence_score, 1)) >= 45 THEN 'critical'
        WHEN (impact_score * urgency_score * GREATEST(confidence_score, 1)) >= 20 THEN 'important'
        ELSE 'watch'
    END
) STORED;
CREATE INDEX recommendation_priority_level_idx ON nexora.recommendation (company_id, priority_level, status);

-- ===================== 2. Expiration effective (§6, §21) ======================
-- La colonne existait dans le cahier des charges mais jamais dans le schéma — une recommandation
-- `new` jamais traitée doit finir par expirer plutôt que de s'accumuler indéfiniment.
ALTER TABLE nexora.recommendation ADD COLUMN expires_at timestamptz;

-- ===================== 3. Action « modifier » avant acceptation (§16, §19) ===
-- L'utilisateur peut ajuster une valeur proposée (ex. quantité suggérée) avant de la valider —
-- jamais une réécriture silencieuse : `override_value` porte ce qui a changé, à côté de
-- `impact_estimate` (la proposition d'origine), jamais à sa place.
ALTER TABLE nexora.recommendation ADD COLUMN override_value jsonb;

ALTER TABLE nexora.recommendation DROP CONSTRAINT recommendation_status_chk;
ALTER TABLE nexora.recommendation ADD CONSTRAINT recommendation_status_chk
    CHECK (status IN ('new', 'accepted', 'dismissed', 'snoozed', 'resolved', 'modified', 'expired'));

-- ===================== 4. Nouvelles catégories de règles (§9.2, §9.4, §9.5) ==
-- Ventes/clients/fournisseurs — les 3 domaines que la Phase 43 documentait explicitement comme
-- non couverts (« seules stock/créances/trésorerie/budget sont couvertes cette phase »).
ALTER TABLE nexora.recommendation DROP CONSTRAINT recommendation_category_chk;
ALTER TABLE nexora.recommendation ADD CONSTRAINT recommendation_category_chk CHECK (category IN (
    'stock_reorder', 'stock_dormant', 'receivable_priority', 'cashflow_risk', 'budget_overrun',
    'sales_spike', 'sales_decline', 'customer_inactive', 'supplier_price_increase'));

-- ===================== 5. Amorçage des 4 nouvelles règles système (§34) ======
INSERT INTO nexora.intelligence_rule (company_id, key, category, label, threshold, period_days, default_priority, action_label) VALUES
    (NULL, 'sales_spike', 'sales', 'Hausse inhabituelle des ventes',
        '{"growthPct":15,"minHistoryDays":10}'::jsonb, 14, 'medium', 'Analyser la hausse et sécuriser le réapprovisionnement'),
    (NULL, 'sales_decline', 'sales', 'Baisse inhabituelle des ventes',
        '{"declinePct":20,"minHistoryDays":10}'::jsonb, 14, 'high', 'Analyser la cause de la baisse'),
    (NULL, 'customer_inactive', 'customers', 'Client devenu inactif',
        '{"inactiveDays":60,"minPriorOrders":3}'::jsonb, NULL, 'medium', 'Relancer le client'),
    (NULL, 'supplier_price_increase', 'suppliers', 'Hausse des prix fournisseur',
        '{"increasePct":10,"minOrdersEachSide":2}'::jsonb, NULL, 'medium', 'Renégocier ou comparer les fournisseurs')
ON CONFLICT ON CONSTRAINT intelligence_rule_key_uk DO NOTHING;

SELECT nexora.attach_common_triggers();
