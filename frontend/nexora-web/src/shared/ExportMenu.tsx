import { useCallback, useState, type ReactNode } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { Button, useClickOutside } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import {
  downloadDocument, exportScreen, printDocument, type PrintableKind, type PrintDocument,
} from "@/shared/printApi";
import { pageDocument, screenKey, tableDocument } from "@/shared/tableExport";

export interface MenuItem { label: string; hint?: string; run: () => Promise<unknown> | void }

const errorOf = (e: unknown) => (e instanceof ApiError ? e.message : "Opération impossible.");

/** Bouton + liste d'actions (imprimer, exporter…) ; affiche l'attente et les erreurs. */
export function ActionMenu({ label, icon, items, size = "sm", variant = "ghost", iconOnly }: {
  label: string; icon?: ReactNode; items: MenuItem[]; size?: "sm"; variant?: "ghost" | "subtle" | "primary"; iconOnly?: boolean;
}) {
  const toast = useToast();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const close = useCallback(() => setOpen(false), []);
  const ref = useClickOutside<HTMLDivElement>(close);

  async function run(item: MenuItem) {
    setOpen(false);
    setBusy(true);
    try {
      await item.run();
    } catch (e) {
      toast.error(errorOf(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div ref={ref} style={{ position: "relative", display: "inline-block" }} data-export="skip">
      {iconOnly ? (
        <button type="button" className="nx-iconbtn" aria-label={label} title={label} aria-haspopup="menu" aria-expanded={open}
                disabled={busy} onClick={() => setOpen((o) => !o)}>
          {busy ? <span className="nx-spinner" style={{ width: 14, height: 14 }} /> : icon}
        </button>
      ) : (
        <Button size={size} variant={variant} loading={busy} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
          {icon}{label}<Icon.ChevronDown size={12} />
        </Button>
      )}
      {open && (
        <div role="menu" className="nx-card"
             style={{ position: "absolute", right: 0, top: "100%", marginTop: 4, zIndex: 40, minWidth: 220, padding: 4, display: "grid" }}>
          {items.map((item) => (
            <button key={item.label} role="menuitem" type="button" onClick={() => void run(item)}
                    style={{ textAlign: "left", background: "transparent", border: 0, padding: "8px 10px", borderRadius: 6,
                             cursor: "pointer", color: "inherit", font: "inherit" }}
                    onMouseEnter={(e) => (e.currentTarget.style.background = "var(--nx-surface-2, rgba(127,127,127,.12))")}
                    onMouseLeave={(e) => (e.currentTarget.style.background = "transparent")}>
              <div>{item.label}</div>
              {item.hint && <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>{item.hint}</div>}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** Impression d'un reçu / document construit par le serveur : A4, ticket 80 mm, PDF ou Excel. */
export function PrintMenu({ kind, id, ticket = false, excel = true, label = "Imprimer", iconOnly }: {
  kind: PrintableKind; id: string; ticket?: boolean; excel?: boolean; label?: string; iconOnly?: boolean;
}) {
  const items: MenuItem[] = [
    { label: "Imprimer (A4)", hint: "Ouvre le PDF dans un onglet : bouton Imprimer ou Ctrl+P", run: () => printDocument(kind, id, "pdf") },
    ...(ticket ? [{ label: "Imprimer le ticket (80 mm)", hint: "Imprimante thermique", run: () => printDocument(kind, id, "ticket") }] : []),
    { label: "Télécharger le PDF", run: () => downloadDocument(kind, id, "pdf") },
    ...(excel ? [{ label: "Télécharger en Excel", run: () => downloadDocument(kind, id, "xlsx") }] : []),
  ];
  return <ActionMenu label={label} icon={<Icon.Printer size={14} />} items={items} iconOnly={iconOnly} />;
}

/** Export Excel / PDF / CSV d'un document construit à la demande. */
export function ExportMenu({ label = "Exporter", run, formats = ["xlsx", "pdf", "csv"], iconOnly }: {
  label?: string; run: (format: "xlsx" | "pdf" | "csv") => Promise<unknown>; formats?: Array<"xlsx" | "pdf" | "csv">; iconOnly?: boolean;
}) {
  const names = { xlsx: "Excel (.xlsx)", pdf: "PDF", csv: "CSV" } as const;
  return (
    <ActionMenu label={label} icon={<Icon.Download size={14} />} iconOnly={iconOnly}
                items={formats.map((f) => ({ label: names[f], run: () => run(f) }))} />
  );
}

/** Outils d'un tableau de données : « Exporter ce tableau » (rendu par DataTable, si reporting.export). */
export function TableExportTools({ table, title }: { table: () => HTMLTableElement | null; title?: string }) {
  const run = (format: "xlsx" | "pdf" | "csv") => {
    const el = table();
    if (!el) return Promise.resolve();
    const doc: PrintDocument = tableDocument(el, title);
    return exportScreen(screenKey(), doc, format);
  };
  return <ExportMenu label="Exporter ce tableau" iconOnly run={run} />;
}

/** Export de la page entière (tous ses tableaux et indicateurs), depuis la barre du haut. */
export function PageExportButton() {
  const { hasPermission } = useAuth();
  const toast = useToast();
  if (!hasPermission("reporting.export")) return null;
  const run = async (format: "xlsx" | "pdf" | "csv") => {
    const doc = pageDocument();
    if ((doc.tables?.length ?? 0) === 0 && (doc.summary?.length ?? 0) === 0) {
      toast.push("Aucun tableau ni indicateur à exporter sur cette page.", "info");
      return;
    }
    await exportScreen(screenKey(), doc, format);
  };
  return <ExportMenu label="Exporter la page (Excel, PDF)" iconOnly run={run} />;
}

/** Fournit l'export aux DataTable de l'application (permission reporting.export). */
export function useTableExportRenderer() {
  const { hasPermission } = useAuth();
  return hasPermission("reporting.export")
    ? (p: { table: () => HTMLTableElement | null; title?: string }) => <TableExportTools table={p.table} title={p.title} />
    : null;
}
