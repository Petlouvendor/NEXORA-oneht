import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { Icon } from "@/shared/icons";
import { AiConversation } from "@/ai/AiConversation";
import { AI_OPEN_EVENT, openNexoraAi } from "@/ai/nexoraAi";

/** Bouton « NEXORA AI » de la barre supérieure — visible seulement avec intelligence.use_assistant. */
export function NexoraAiButton() {
  const { hasPermission } = useAuth();
  if (!hasPermission("intelligence.use_assistant")) return null;
  return (
    <button type="button" className="nx-iconbtn nx-ai-launch" onClick={() => openNexoraAi()}
            aria-label="Ouvrir NEXORA AI (Ctrl+J)" title="NEXORA AI (Ctrl+J)">
      <Icon.Sparkles size={15} /> <span className="nx-hide-sm">NEXORA AI</span>
    </button>
  );
}

/**
 * NEXORA AI (brief §8) — panneau latéral global. S'ouvre par le bouton, Ctrl+J, ou l'événement
 * `nexora-ai:open` (Ctrl+K « Demander à NEXORA AI », dashboard). La conversation est conservée tant
 * que la session de l'écran dure ; fermer le panneau ne l'efface pas.
 */
export function NexoraAiPanel() {
  const { hasPermission, activeCompanyId } = useAuth();
  const allowed = hasPermission("intelligence.use_assistant");
  const [open, setOpen] = useState(false);
  const [question, setQuestion] = useState<{ text: string; nonce: number } | null>(null);
  const nonce = useRef(0);

  useEffect(() => {
    if (!allowed) return;
    const onOpen = (e: Event) => {
      const q = (e as CustomEvent<{ question?: string }>).detail?.question;
      setOpen(true);
      if (q) setQuestion({ text: q, nonce: ++nonce.current });
    };
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "j") { e.preventDefault(); setOpen((v) => !v); }
      else if (e.key === "Escape") setOpen(false);
    };
    window.addEventListener(AI_OPEN_EVENT, onOpen);
    window.addEventListener("keydown", onKey);
    return () => { window.removeEventListener(AI_OPEN_EVENT, onOpen); window.removeEventListener("keydown", onKey); };
  }, [allowed]);

  // Changer d'entreprise = autres données : la conversation précédente ne doit pas rester affichée.
  useEffect(() => { setQuestion(null); }, [activeCompanyId]);

  // Le panneau reste monté (conversation conservée) : le focus clavier est donné à chaque ouverture.
  useEffect(() => {
    if (!open) return;
    const t = setTimeout(() => document.querySelector<HTMLInputElement>(".nx-ai-panel__input input")?.focus(), 30);
    return () => clearTimeout(t);
  }, [open]);

  if (!allowed) return null;
  return (
    <div className="nx-drawer-wrap" style={{ display: open ? undefined : "none" }}>
      <div className="nx-overlay" style={{ position: "absolute" }} onMouseDown={() => setOpen(false)} />
      <aside className="nx-ai-panel" role="dialog" aria-modal="true" aria-label="NEXORA AI">
        <div className="nx-ai-panel__head">
          <span className="nx-ai-orb"><Icon.Sparkles size={15} /></span>
          <div style={{ flex: 1 }}>
            <div style={{ fontWeight: 750, letterSpacing: "0.04em" }}>NEXORA AI</div>
            <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-xs)" }}>
              Calculé sur vos données · sources citées · aucune décision automatique
            </div>
          </div>
          <span className="nx-kbd">Ctrl</span><span className="nx-kbd">J</span>
          <button type="button" className="nx-iconbtn" aria-label="Fermer NEXORA AI" onClick={() => setOpen(false)}><Icon.X /></button>
        </div>
        <AiConversation key={activeCompanyId ?? "none"} question={question} onNavigate={() => setOpen(false)} />
      </aside>
    </div>
  );
}
