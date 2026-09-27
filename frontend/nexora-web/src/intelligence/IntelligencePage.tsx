import { useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { Button, Card, Tabs } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { openNexoraAi } from "@/ai/nexoraAi";
import {
  AssistantSection, IntelligenceDashboardSection, ModelsSection, RecommendationOutcomesSection,
  RecommendationsSection, RulesSection, ScoresSection, TasksSection,
} from "@/intelligence/IntelligenceSections";

/**
 * NEXORA INTELLIGENCE (NEXORA ONE §7 ; Phase 43/54) — analyse ventes, stock, achats, finance, clients,
 * fournisseurs et produit insights, alertes, anomalies, prévisions et recommandations expliquées
 * (QUOI / POURQUOI / IMPACT / ACTION). Rien n'est exécuté sans décision humaine.
 */
export function IntelligencePage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("dashboard");

  const canView = hasPermission("intelligence.view");
  const canAct = hasPermission("intelligence.act_on_recommendation");
  const canManageRules = hasPermission("intelligence.manage_rules");
  const canUseAssistant = hasPermission("intelligence.use_assistant");

  if (!canView && !canUseAssistant) {
    return <Card>L'intelligence métier requiert la permission <code>intelligence.view</code> ou <code>intelligence.use_assistant</code>.</Card>;
  }

  const tabs = [
    canView && { id: "dashboard", label: "Vue générale" },
    canView && { id: "recommendations", label: "Recommandations" },
    canView && { id: "outcomes", label: "Boucle d'apprentissage" },
    canView && { id: "scores", label: "Scores" },
    canView && { id: "tasks", label: "Tâches" },
    canView && { id: "models", label: "Gouvernance" },
    canView && { id: "rules", label: "Règles" },
    canUseAssistant && { id: "assistant", label: "NEXORA AI" },
  ].filter((t): t is { id: string; label: string } => Boolean(t));
  const active = tabs.some((t) => t.id === tab) ? tab : tabs[0].id;

  return (
    <div style={{ display: "grid", gap: 20 }}>
      <header style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: 12, flexWrap: "wrap" }}>
        <div>
          <div className="nx-eyebrow nx-eyebrow--brand"><Icon.Sparkles size={13} /> NEXORA Intelligence</div>
          <h1 style={{ margin: "4px 0 0" }}>Comprendre, anticiper, décider</h1>
          <div style={{ color: "var(--nx-text-2)" }}>
            Données → information → insight → recommandation → action. Chaque proposition explique son pourquoi et son impact.
          </div>
        </div>
        {canUseAssistant && (
          <Button variant="ghost" onClick={() => openNexoraAi()}><Icon.Sparkles size={15} /> Demander à NEXORA AI</Button>
        )}
      </header>
      <Tabs tabs={tabs} active={active} onChange={setTab} />

      {active === "dashboard" && canView && <IntelligenceDashboardSection canAct={canAct} onOpenList={() => setTab("recommendations")} />}
      {active === "recommendations" && canView && <RecommendationsSection canAct={canAct} />}
      {active === "outcomes" && canView && <RecommendationOutcomesSection canAct={canAct} />}
      {active === "scores" && canView && <ScoresSection />}
      {active === "tasks" && canView && <TasksSection />}
      {active === "models" && canView && <ModelsSection />}
      {active === "rules" && canView && <RulesSection canManage={canManageRules} />}
      {active === "assistant" && canUseAssistant && <AssistantSection />}
    </div>
  );
}
