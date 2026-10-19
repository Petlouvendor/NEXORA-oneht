import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { usePreferences } from "@/shared/PreferencesContext";
import { ALL_NAV } from "@/app/nav";
import { Icon } from "@/shared/icons";
import { globalSearch, type SearchGroup } from "@/app/experienceApi";
import { buildPalette, type PaletteItem } from "@/app/paletteModel";
import { openNexoraAi } from "@/ai/nexoraAi";

/**
 * Palette de commande Ctrl+K (NEXORA ONE §5) : produits, clients, fournisseurs, employés, factures,
 * ventes (recherche serveur filtrée par permission), pages/rapports, actions et paramètres — et
 * « Demander à NEXORA AI » pour toute question libre.
 */
export function CommandPalette() {
  const { hasPermission, activeCompanyId } = useAuth();
  const { toggleTheme } = usePreferences();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState("");
  const [sel, setSel] = useState(0);
  const [search, setSearch] = useState<SearchGroup[]>([]);
  const [searching, setSearching] = useState(false);
  const listRef = useRef<HTMLDivElement>(null);
  const seq = useRef(0);

  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setOpen((v) => !v);
      }
      if (e.key === "Escape") setOpen(false);
    };
    window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, []);

  useEffect(() => { if (open) { setQ(""); setSel(0); setSearch([]); } }, [open]);

  // Recherche serveur temporisée ; une réponse périmée (frappe plus récente) est ignorée.
  useEffect(() => {
    const term = q.trim();
    if (!open || term.length < 2) { setSearch([]); setSearching(false); return; }
    const id = ++seq.current;
    setSearching(true);
    const t = setTimeout(() => {
      globalSearch(term)
        .then((r) => { if (id === seq.current) setSearch(r.groups); })
        .catch(() => { if (id === seq.current) setSearch([]); })
        .finally(() => { if (id === seq.current) setSearching(false); });
    }, 180);
    return () => clearTimeout(t);
  }, [q, open, activeCompanyId]);

  const sections = useMemo(
    () => buildPalette({ query: q, nav: ALL_NAV, search, can: hasPermission }),
    [q, search, hasPermission],
  );
  const flat = useMemo(() => sections.flatMap((s) => s.items), [sections]);
  useEffect(() => { setSel((s) => Math.min(s, Math.max(0, flat.length - 1))); }, [flat.length]);
  useEffect(() => {
    listRef.current?.querySelector<HTMLElement>('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" });
  }, [sel]);

  if (!open) return null;

  function run(item: PaletteItem | undefined, to?: string) {
    if (!item) return;
    setOpen(false);
    if (to) { void navigate(to); return; }
    const e = item.effect;
    if (e.type === "route") void navigate(e.to);
    else if (e.type === "ask") openNexoraAi(e.question);
    else toggleTheme();
  }

  let index = -1;
  return (
    <div className="nx-overlay" style={{ alignItems: "flex-start" }} onMouseDown={(e) => e.target === e.currentTarget && setOpen(false)}>
      <div className="nx-palette" role="dialog" aria-modal="true" aria-label="Recherche globale">
        <div style={{ display: "flex", alignItems: "center", gap: 8, paddingLeft: 14, borderBottom: "1px solid var(--nx-border)" }}>
          <span style={{ color: searching ? "var(--nx-brand)" : "var(--nx-text-3)" }}><Icon.Search size={18} /></span>
          <input
            className="nx-input" autoFocus role="combobox" aria-expanded="true" aria-controls="nx-palette-list"
            placeholder="Produit, client, fournisseur, employé, facture, vente, rapport, action…"
            value={q}
            onChange={(e) => { setQ(e.target.value); setSel(0); }}
            onKeyDown={(e) => {
              if (e.key === "ArrowDown") { e.preventDefault(); setSel((s) => Math.min(s + 1, flat.length - 1)); }
              if (e.key === "ArrowUp") { e.preventDefault(); setSel((s) => Math.max(s - 1, 0)); }
              if (e.key === "Enter") run(flat[sel]);
            }}
            style={{ padding: "16px 8px", fontSize: "var(--nx-fs-lg)", borderBottom: "none" }}
          />
          <span className="nx-kbd" style={{ marginRight: 12 }}>Échap</span>
        </div>
        <div className="nx-palette__list" id="nx-palette-list" role="listbox" ref={listRef}>
          {flat.length === 0 && (
            <div style={{ padding: 16, color: "var(--nx-text-2)" }}>
              {searching ? "Recherche…" : `Aucun résultat pour « ${q} ».`}
            </div>
          )}
          {sections.map((section) => (
            <div key={section.title} role="group" aria-label={section.title}>
              <div className="nx-palette__group">{section.title}</div>
              {section.items.map((item) => {
                index += 1;
                const i = index;
                const I = Icon[item.icon];
                const selected = i === sel;
                return (
                  <div key={item.id}>
                    <div className="nx-palette__item" role="option" aria-selected={selected}
                         onMouseEnter={() => setSel(i)} onClick={() => run(item)}>
                      <span style={{ color: item.effect.type === "ask" ? "var(--nx-brand)" : "var(--nx-text-2)" }}><I size={16} /></span>
                      <span style={{ display: "grid", minWidth: 0 }}>
                        <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{item.label}</span>
                        {item.hint && <small>{item.hint}</small>}
                      </span>
                      {selected && <span className="nx-kbd" style={{ marginLeft: "auto" }}>⏎</span>}
                    </div>
                    {selected && item.links && item.links.length > 0 && (
                      <div className="nx-palette__links">
                        {item.links.map((l) => (
                          <button key={l.label} type="button" className="nx-chip" onClick={() => run(item, l.to)}>
                            {l.label} <Icon.ArrowRight size={12} />
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          ))}
        </div>
        <div style={{ display: "flex", gap: 14, padding: "8px 14px", borderTop: "1px solid var(--nx-border)", color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)" }}>
          <span><span className="nx-kbd">↑</span> <span className="nx-kbd">↓</span> naviguer</span>
          <span><span className="nx-kbd">⏎</span> ouvrir</span>
          {hasPermission("intelligence.use_assistant") && <span><span className="nx-kbd">Ctrl</span> <span className="nx-kbd">J</span> NEXORA AI</span>}
        </div>
      </div>
    </div>
  );
}
