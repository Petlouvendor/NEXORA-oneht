import {
  cloneElement, createContext, isValidElement, useContext, useEffect, useId, useRef, useState,
  type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type Ref, type SelectHTMLAttributes,
} from "react";
import { Icon } from "@/shared/icons";
import { money as fmtMoney } from "@/shared/format";

/* ---------------------------------------------------------------- Boutons */

type BtnVariant = "primary" | "ghost" | "subtle" | "danger";
export function Button({
  variant = "primary", size, block, loading, children, className, disabled, ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: BtnVariant; size?: "sm"; block?: boolean; loading?: boolean;
}) {
  const cls = [
    "nx-btn",
    variant !== "primary" && `nx-btn--${variant}`,
    size === "sm" && "nx-btn--sm",
    block && "nx-btn--block",
    className,
  ].filter(Boolean).join(" ");
  return (
    <button className={cls} disabled={disabled || loading} aria-busy={loading} {...rest}>
      {loading && <span className="nx-spinner" style={{ width: 14, height: 14 }} />}
      {children}
    </button>
  );
}

export function IconButton({
  label, children, ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { label: string }) {
  return (
    <button type="button" className="nx-iconbtn" aria-label={label} title={label} {...rest}>
      {children}
    </button>
  );
}

/* --------------------------------------------------------------- Champs */

export function Field({
  label, required, hint, error, children,
}: { label: string; required?: boolean; hint?: string; error?: string | null; children: ReactNode }) {
  // Recette Phase 55 UX-012 : le libellé n'était relié à aucun champ — invisible pour un lecteur
  // d'écran et non cliquable. On relie le champ (id + htmlFor) et on y attache l'aide / l'erreur.
  const autoId = useId();
  const child = isValidElement<{ id?: string; "aria-describedby"?: string; "aria-required"?: boolean }>(children) ? children : null;
  const id = child?.props.id ?? `f${autoId}`;
  const describedBy = error ? `${id}-err` : hint ? `${id}-hint` : undefined;
  return (
    <div className="nx-field">
      <label htmlFor={child ? id : undefined}>{label}{required && <span className="nx-req" aria-hidden>*</span>}</label>
      {child
        ? cloneElement(child, {
            id,
            "aria-describedby": [child.props["aria-describedby"], describedBy].filter(Boolean).join(" ") || undefined,
            ...(required ? { "aria-required": true } : {}),
          })
        : children}
      {hint && !error && <span className="nx-hint" id={`${id}-hint`}>{hint}</span>}
      {error && <span className="nx-err" id={`${id}-err`} role="alert">{error}</span>}
    </div>
  );
}

export function TextInput({
  invalid, ref, ...rest
}: InputHTMLAttributes<HTMLInputElement> & { invalid?: boolean; ref?: Ref<HTMLInputElement> }) {
  return <input ref={ref} className="nx-input" aria-invalid={invalid || undefined} {...rest} />;
}

export function Select({ children, ...rest }: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select className="nx-select" {...rest}>{children}</select>;
}

/* --------------------------------------------------------------- Carte / KPI */

export function Card({ children, pad, className, ...rest }: { children: ReactNode; pad?: "lg"; className?: string } & React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div className={["nx-card", pad === "lg" && "nx-card--pad-lg", className].filter(Boolean).join(" ")} {...rest}>
      {children}
    </div>
  );
}

export function SectionLabel({ children, action }: { children: ReactNode; action?: ReactNode }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 10 }}>
      <div style={{ fontSize: "var(--nx-fs-xs)", textTransform: "uppercase", letterSpacing: 0.5, color: "var(--nx-text-2)" }}>
        {children}
      </div>
      {action}
    </div>
  );
}

export function KpiCard({
  label, value, delta, tag, tone,
}: { label: string; value: ReactNode; delta?: { value: string; positive: boolean }; tag?: string; tone?: "warning" }) {
  return (
    <Card className="nx-kpi">
      <div className="nx-kpi__label">
        {label}
        {tag && <Badge tone={tone === "warning" ? "warning" : undefined} style={{ marginLeft: 6 }}>{tag}</Badge>}
      </div>
      <div className="nx-kpi__value nx-numeric">{value}</div>
      {delta && (
        <div className="nx-kpi__delta" style={{ color: delta.positive ? "var(--nx-pos)" : "var(--nx-neg)" }}>
          {delta.positive ? "▲" : "▼"} {delta.value}
        </div>
      )}
    </Card>
  );
}

/* --------------------------------------------------------------- Badges / états */

type Tone = "success" | "warning" | "error" | "info";
export function Badge({
  tone, children, style,
}: { tone?: Tone; children: ReactNode; style?: React.CSSProperties }) {
  return (
    <span className={["nx-badge", tone && `nx-badge--${tone}`].filter(Boolean).join(" ")} style={style}>
      {children}
    </span>
  );
}

const STATE_TONE: Record<string, Tone | undefined> = {
  active: "success", confirmed: "success", processed: "success", delivered: "success",
  reconciled: "success", approved: "success", posted: "success", paid: "success", open: "info",
  synced: "success", syncing: "info",
  pending: "warning", reconciling: "warning", partially_paid: "warning", draft: "warning", conflict: "warning",
  failed: "error", error: "error", exhausted: "error", rejected: "error", overdue: "error",
  amount_mismatch: "error", expired: "error", cancelled: "error", void: "error", revoked: "error",
  pending_approval: "warning", closing: "warning", executed: "success", consumed: "success",
};
export function StatePill({ status }: { status: string }) {
  const tone = STATE_TONE[status.toLowerCase()];
  return <Badge tone={tone}><span className="nx-badge__dot" />{status}</Badge>;
}

/* --------------------------------------------------------------- Money */

export function Money({ value, currency = "HTG", sign }: { value: number; currency?: string; sign?: boolean }) {
  const neg = value < 0;
  return (
    <span className="nx-numeric" style={{ color: sign ? (neg ? "var(--nx-neg)" : "var(--nx-pos)") : undefined }}>
      {fmtMoney(value, currency, { sign })}
    </span>
  );
}

/* --------------------------------------------------------------- Onglets */

export function Tabs({
  tabs, active, onChange,
}: { tabs: Array<{ id: string; label: string }>; active: string; onChange: (id: string) => void }) {
  return (
    <div role="tablist" style={{ display: "flex", gap: 4, borderBottom: "1px solid var(--nx-border)", flexWrap: "wrap" }}>
      {tabs.map((tb) => (
        <button
          key={tb.id} role="tab" aria-selected={tb.id === active}
          onClick={() => onChange(tb.id)}
          style={{
            appearance: "none", background: "transparent", border: "none", cursor: "pointer",
            font: "inherit", fontWeight: 600, padding: "9px 12px", color: tb.id === active ? "var(--nx-text)" : "var(--nx-text-2)",
            borderBottom: `2px solid ${tb.id === active ? "var(--nx-brand)" : "transparent"}`, marginBottom: -1,
          }}
        >
          {tb.label}
        </button>
      ))}
    </div>
  );
}

/* --------------------------------------------------------------- Modal / Drawer */

function useEscape(onClose: () => void) {
  useEffect(() => {
    const h = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", h);
    return () => document.removeEventListener("keydown", h);
  }, [onClose]);
}

export function Modal({
  title, onClose, children, footer,
}: { title: string; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  useEscape(onClose);
  return (
    <div className="nx-overlay" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="nx-modal" role="dialog" aria-modal="true" aria-label={title}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: 12, marginBottom: 14 }}>
          <h2 style={{ margin: 0 }}>{title}</h2>
          <IconButton label="Fermer" onClick={onClose}><Icon.X /></IconButton>
        </div>
        <div>{children}</div>
        {footer && <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", marginTop: 18 }}>{footer}</div>}
      </div>
    </div>
  );
}

export function Drawer({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  useEscape(onClose);
  return (
    <div className="nx-drawer-wrap" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="nx-overlay" style={{ position: "absolute" }} />
      <aside className="nx-drawer" role="dialog" aria-modal="true" aria-label={title}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 16 }}>
          <h3 style={{ margin: 0 }}>{title}</h3>
          <IconButton label="Fermer" onClick={onClose}><Icon.X /></IconButton>
        </div>
        {children}
      </aside>
    </div>
  );
}

/** Confirmation d'action sensible (§20) — résumé + motif optionnel. */
export function ConfirmDialog({
  title, message, impact, confirmLabel = "Confirmer", danger, requireReason, onConfirm, onCancel,
}: {
  title: string; message: string; impact?: ReactNode; confirmLabel?: string; danger?: boolean;
  requireReason?: boolean; onConfirm: (reason: string) => void; onCancel: () => void;
}) {
  const [reason, setReason] = useState("");
  return (
    <Modal
      title={title}
      onClose={onCancel}
      footer={
        <>
          <Button variant="ghost" onClick={onCancel}>Annuler</Button>
          <Button
            variant={danger ? "danger" : "primary"}
            disabled={requireReason && reason.trim().length < 3}
            onClick={() => onConfirm(reason.trim())}
          >
            {confirmLabel}
          </Button>
        </>
      }
    >
      <p style={{ marginTop: 0 }}>{message}</p>
      {impact && (
        <div className="nx-card" style={{ background: "var(--nx-warning-bg)", borderColor: "transparent", marginBottom: 12 }}>
          {impact}
        </div>
      )}
      {requireReason && (
        <Field label="Motif" required hint="Consigné dans le journal d'audit.">
          <textarea className="nx-textarea" rows={3} value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
      )}
    </Modal>
  );
}

/* --------------------------------------------------------------- États */

export function EmptyState({ title, hint, action }: { title: string; hint?: string; action?: ReactNode }) {
  return (
    <div className="nx-empty">
      <Icon.Info size={26} />
      <h3 style={{ margin: 0 }}>{title}</h3>
      {hint && <div style={{ maxWidth: 340 }}>{hint}</div>}
      {action}
    </div>
  );
}

export function Skeleton({ rows = 3 }: { rows?: number }) {
  return (
    <div style={{ display: "grid", gap: 8 }}>
      {Array.from({ length: rows }).map((_, i) => (
        <div key={i} className="nx-skeleton" style={{ height: 16, width: `${70 + ((i * 37) % 30)}%` }} />
      ))}
    </div>
  );
}

export function Spinner({ label = "Chargement…" }: { label?: string }) {
  return (
    <div style={{ display: "flex", gap: 10, alignItems: "center", color: "var(--nx-text-2)" }}>
      <span className="nx-spinner" /> {label}
    </div>
  );
}

export function Tooltip({ text, children }: { text: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const id = useId();
  return (
    <span
      className="nx-tip"
      onMouseEnter={() => setOpen(true)} onMouseLeave={() => setOpen(false)}
      onFocus={() => setOpen(true)} onBlur={() => setOpen(false)}
    >
      <span aria-describedby={open ? id : undefined}>{children}</span>
      {open && <span role="tooltip" id={id} className="nx-tip__bubble">{text}</span>}
    </span>
  );
}

/** Aide contextuelle brève (§39). */
export function HelpTip({ text }: { text: string }) {
  return (
    <Tooltip text={text}>
      <span style={{ color: "var(--nx-text-3)", cursor: "help", display: "inline-flex" }}><Icon.Info size={14} /></span>
    </Tooltip>
  );
}

/* --------------------------------------------------------------- Table de données */

export interface Column<T> {
  key: string;
  header: string;
  render: (row: T) => ReactNode;
  numeric?: boolean;
}

/** Outils d'un tableau (export Excel / PDF) fournis par l'application selon les permissions — le kit
 * d'interface ne dépend ainsi ni de l'API ni de l'authentification. */
export type TableToolsRenderer = (p: { table: () => HTMLTableElement | null; title?: string }) => ReactNode;
export const TableToolsContext = createContext<TableToolsRenderer | null>(null);

export function DataTable<T>({
  columns, rows, rowKey, empty, footer, exportTitle,
}: {
  columns: Array<Column<T>>; rows: T[]; rowKey: (row: T) => string;
  empty?: ReactNode; footer?: ReactNode;
  /** Titre du fichier exporté (défaut : titre de la carte ou de la page) ; <c>false</c> = pas d'export. */
  exportTitle?: string | false;
}) {
  const tools = useContext(TableToolsContext);
  const ref = useRef<HTMLTableElement>(null);
  return (
    <div className="nx-scroll-x">
      {tools && exportTitle !== false && rows.length > 0 && (
        <div className="nx-table-tools" style={{ display: "flex", justifyContent: "flex-end", marginBottom: 2 }}>
          {tools({ table: () => ref.current, title: exportTitle || undefined })}
        </div>
      )}
      <table className="nx-table" ref={ref}>
        <thead>
          <tr>{columns.map((c) => <th key={c.key} className={c.numeric ? "nx-num" : undefined}>{c.header}</th>)}</tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr><td colSpan={columns.length} style={{ color: "var(--nx-text-2)", padding: 20, textAlign: "center" }}>
              {empty ?? "Rien à afficher."}
            </td></tr>
          ) : rows.map((row) => (
            <tr key={rowKey(row)}>
              {columns.map((c) => <td key={c.key} className={c.numeric ? "nx-num" : undefined}>{c.render(row)}</td>)}
            </tr>
          ))}
        </tbody>
        {footer && <tfoot><tr>{footer}</tr></tfoot>}
      </table>
    </div>
  );
}

/* --------------------------------------------------------------- Détecteur de clic extérieur */

export function useClickOutside<T extends HTMLElement>(onOutside: () => void) {
  const ref = useRef<T>(null);
  useEffect(() => {
    const h = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onOutside();
    };
    document.addEventListener("mousedown", h);
    return () => document.removeEventListener("mousedown", h);
  }, [onOutside]);
  return ref;
}
