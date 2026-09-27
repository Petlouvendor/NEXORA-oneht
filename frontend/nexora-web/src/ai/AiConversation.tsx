import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { ApiError } from "@/core/apiClient";
import { Badge, Button } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { BarList, PRIORITY } from "@/shared/intel";
import { askAssistant, type AssistantAnswer } from "@/intelligence/intelligenceApi";
import { AI_SUGGESTIONS, formatFact, sourceLink, splitFacts, structureAnswer } from "@/ai/nexoraAi";

interface Turn { id: number; question: string; answer?: AssistantAnswer; error?: string }

/** Réponse de NEXORA AI (§8) : explication → causes, données, graphique, sources, recommandations. */
export function AiAnswerView({ answer, onNavigate }: { answer: AssistantAnswer; onNavigate?: () => void }) {
  if (answer.intent === "forbidden") {
    return (
      <div className="nx-ai-a" style={{ borderColor: "var(--nx-warning)" }}>
        <div style={{ display: "flex", gap: 8, alignItems: "center" }}><Icon.Shield size={16} /> {answer.answer}</div>
      </div>
    );
  }
  const blocks = structureAnswer(answer.answer);
  const [lead, ...rest] = blocks;
  const { chart, tiles } = splitFacts(answer.facts);
  const sources = answer.sources.map(sourceLink).filter((s, i, all) => all.findIndex((o) => o.to === s.to && o.label === s.label) === i);
  return (
    <div className="nx-ai-a">
      {lead && (lead.text
        ? <div className="nx-ai-a__lead">{lead.text}</div>
        : <div><div className="nx-ai-a__lead">{lead.label}</div><ol>{lead.items.map((i) => <li key={i}>{i}</li>)}</ol></div>)}
      {rest.map((b, idx) => b.text
        ? <p key={idx} style={{ margin: 0, color: "var(--nx-text-2)" }}>{b.text}</p>
        : (
          <div key={idx}>
            <div className="nx-eyebrow" style={{ marginBottom: 4 }}>{b.label}</div>
            <ol>{b.items.map((i) => <li key={i}>{i}</li>)}</ol>
          </div>
        ))}

      {answer.containsForecast && <div><Badge tone="warning">Estimation — pas une certitude</Badge></div>}

      {chart.length > 0 && (
        <BarList rows={chart.map((f) => ({ key: f.key + f.label, label: f.label, value: f.value, display: formatFact(f) }))} />
      )}
      {tiles.length > 0 && (
        <div className="nx-insight__metrics">
          {tiles.map((f) => (
            <div key={f.key + f.label} className="nx-metric">
              <div className="nx-metric__label">{f.label}</div>
              <div className="nx-metric__value">{formatFact(f)}</div>
            </div>
          ))}
        </div>
      )}

      {answer.related && answer.related.length > 0 && (
        <div style={{ display: "grid", gap: 6 }}>
          <div className="nx-eyebrow nx-eyebrow--brand"><Icon.Sparkles size={13} /> NEXORA recommande</div>
          {answer.related.map((r) => {
            const p = PRIORITY[r.priorityLevel] ?? PRIORITY.watch;
            return (
              <Link key={r.id} to="/intelligence" onClick={onNavigate} className="nx-insight__action" style={{ textDecoration: "none" }}>
                <Badge tone={p.tone}>{p.label}</Badge>
                <span style={{ display: "grid" }}>
                  <span>{r.title}</span>
                  <span style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)", fontWeight: 500 }}>{r.proposedAction}</span>
                </span>
              </Link>
            );
          })}
        </div>
      )}

      {sources.length > 0 && (
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap", alignItems: "center" }}>
          <span className="nx-eyebrow">Sources</span>
          {sources.map((s) => <Link key={s.label + s.to} to={s.to} className="nx-chip" onClick={onNavigate}>{s.label}</Link>)}
          <Link to={sources[0].to} onClick={onNavigate} style={{ marginLeft: "auto" }}>
            <Button size="sm" variant="ghost">Voir l'analyse <Icon.ArrowRight size={14} /></Button>
          </Link>
        </div>
      )}
    </div>
  );
}

/**
 * Conversation NEXORA AI. `question` (avec `nonce`) permet à un appelant (Ctrl+K, Dashboard) de poser une
 * question dans la conversation en cours. Chaque question est journalisée côté serveur (assistant_query).
 */
export function AiConversation({ question, onNavigate, autoFocus }: {
  question?: { text: string; nonce: number } | null; onNavigate?: () => void; autoFocus?: boolean;
}) {
  const [turns, setTurns] = useState<Turn[]>([]);
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);
  const seq = useRef(0);

  async function ask(text: string) {
    const q = text.trim();
    if (!q || busy) return;
    const id = ++seq.current;
    setTurns((t) => [...t, { id, question: q }]);
    setDraft("");
    setBusy(true);
    try {
      const answer = await askAssistant(q);
      setTurns((t) => t.map((x) => (x.id === id ? { ...x, answer } : x)));
    } catch (e) {
      setTurns((t) => t.map((x) => (x.id === id ? { ...x, error: e instanceof ApiError ? e.message : "NEXORA AI est indisponible pour le moment." } : x)));
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    if (question?.text) void ask(question.text);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [question?.nonce]);

  useEffect(() => { endRef.current?.scrollIntoView({ block: "end", behavior: "smooth" }); }, [turns]);

  function submit(e: FormEvent) {
    e.preventDefault();
    void ask(draft);
  }

  return (
    <>
      <div className="nx-ai-panel__body">
        {turns.length === 0 && (
          <div style={{ display: "grid", gap: 14 }}>
            <div style={{ display: "flex", gap: 10, alignItems: "center" }}>
              <span className="nx-ai-orb"><Icon.Sparkles size={15} /></span>
              <div>
                <div style={{ fontWeight: 700 }}>Que voulez-vous comprendre ?</div>
                <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
                  Réponses calculées sur vos données réelles, avec leurs sources — dans la limite de vos droits.
                </div>
              </div>
            </div>
            <div style={{ display: "flex", flexWrap: "wrap", gap: 6 }}>
              {AI_SUGGESTIONS.map((s) => <button key={s} type="button" className="nx-chip" onClick={() => void ask(s)}>{s}</button>)}
            </div>
          </div>
        )}
        {turns.map((t) => (
          <div key={t.id} style={{ display: "grid", gap: 10 }}>
            <div className="nx-ai-q">{t.question}</div>
            {t.answer && <AiAnswerView answer={t.answer} onNavigate={onNavigate} />}
            {t.answer?.intent === "unknown" && (
              <div style={{ display: "flex", flexWrap: "wrap", gap: 6 }}>
                {AI_SUGGESTIONS.slice(0, 4).map((s) => <button key={s} type="button" className="nx-chip" onClick={() => void ask(s)}>{s}</button>)}
              </div>
            )}
            {t.error && <div className="nx-error-text" role="alert">{t.error}</div>}
            {!t.answer && !t.error && (
              <div style={{ display: "flex", gap: 10, alignItems: "center", color: "var(--nx-text-2)" }}>
                <span className="nx-ai-orb"><Icon.Sparkles size={13} /></span>
                <span className="nx-ai-thinking" aria-label="Analyse en cours"><i /><i /><i /></span>
              </div>
            )}
          </div>
        ))}
        <div ref={endRef} />
      </div>
      <form className="nx-ai-panel__input" onSubmit={submit}>
        <input className="nx-input" value={draft} onChange={(e) => setDraft(e.target.value)} autoFocus={autoFocus}
               placeholder="Posez une question sur votre entreprise…" aria-label="Question à NEXORA AI" maxLength={300} />
        <Button type="submit" disabled={busy || !draft.trim()} aria-label="Envoyer"><Icon.Send size={16} /></Button>
      </form>
    </>
  );
}
