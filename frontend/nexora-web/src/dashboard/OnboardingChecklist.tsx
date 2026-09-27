import { useState } from "react";
import { Link } from "react-router-dom";
import { Card, IconButton, SectionLabel } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { useAuth } from "@/auth/AuthContext";

const KEY = "nexora.onboarding.dismissed";

/** Checklist de démarrage (§38) — repliable, « à terminer plus tard ». */
const STEPS: Array<{ label: string; to: string; done?: boolean }> = [
  { label: "Configurer l'entreprise et l'accent", to: "/settings" },
  { label: "Ajouter ou importer des produits", to: "/inventory" },
  { label: "Créer les utilisateurs et rôles", to: "/security" },
  { label: "Paramétrer la caisse", to: "/pos" },
  { label: "Enregistrer une première vente", to: "/pos" },
];

export function OnboardingChecklist() {
  const { hasPermission } = useAuth();
  const [dismissed, setDismissed] = useState(() => localStorage.getItem(KEY) === "1");
  // Mise en service de l'entreprise : tâche d'administration, jamais montrée à un caissier (guide §18).
  if (dismissed || !(hasPermission("admin.settings") || hasPermission("admin.configure_company"))) return null;

  return (
    <Card style={{ borderColor: "var(--nx-brand)" }}>
      <SectionLabel action={
        <IconButton label="Masquer la checklist"
                    onClick={() => { localStorage.setItem(KEY, "1"); setDismissed(true); }}>
          <Icon.X size={14} />
        </IconButton>
      }>
        Démarrage — 5 étapes
      </SectionLabel>
      <ol style={{ margin: 0, paddingLeft: 0, listStyle: "none", display: "grid", gap: 8 }}>
        {STEPS.map((s, i) => (
          <li key={s.to + i} style={{ display: "flex", alignItems: "center", gap: 10 }}>
            <span style={{
              width: 20, height: 20, borderRadius: "50%", flexShrink: 0,
              border: `1.5px solid ${s.done ? "var(--nx-success)" : "var(--nx-border-strong)"}`,
              display: "grid", placeItems: "center", color: "var(--nx-success)",
            }}>
              {s.done ? <Icon.Check size={12} /> : <span style={{ fontSize: 11, color: "var(--nx-text-3)" }}>{i + 1}</span>}
            </span>
            <Link to={s.to} style={{ color: "var(--nx-text)" }}>{s.label}</Link>
          </li>
        ))}
      </ol>
    </Card>
  );
}
