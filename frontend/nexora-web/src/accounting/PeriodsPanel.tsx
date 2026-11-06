import { useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { dateShort, money, todayIso } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Field, Modal, SectionLabel, Select } from "@/shared/ui";
import {
  closePeriod, closeYear, generateOpeningBalances, nextYearState, openNextYear, previewCloseYear, reopenPeriod,
  type AccountingPeriod, type YearEndControl,
} from "@/accounting/accountingApi";

const PERIOD_STATUS: Record<string, { label: string; tone: "success" | "warning" | "info" | "error" }> = {
  open: { label: "Ouverte", tone: "success" },
  closed: { label: "Clôturée", tone: "info" },
  locked: { label: "Verrouillée", tone: "error" },
};
const CONTROL_TONE = { pass: "success", warn: "warning", fail: "error" } as const;
const CONTROL_LABEL = { pass: "OK", warn: "Attention", fail: "Bloquant" } as const;

/** Périodes comptables (clôture / réouverture motivée) et clôture d'exercice avec à-nouveaux. */
export function PeriodsPanel({ periods, onChanged }: { periods: AccountingPeriod[]; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [periodId, setPeriodId] = useState("");
  const selected = periods.find((p) => p.id === periodId) ?? periods.find((p) => p.status === "open") ?? periods[periods.length - 1];
  const report = useAsync(() => (selected ? previewCloseYear(selected.id) : Promise.resolve(null)), [selected?.id, selected?.status]);
  const [dialog, setDialog] = useState<{ kind: "close" | "reopen" | "year" | "opening" | "next"; period: AccountingPeriod } | null>(null);
  const { busy, error, submit } = useSubmit();
  const r = report.data;
  const done = () => { report.reload(); onChanged(); };
  const next = nextYearState(periods, todayIso());
  const canManage = hasPermission("accounting.close_period");

  return (
    <div style={{ display: "grid", gap: 12 }}>
      <Card>
        <div style={{ display: "flex", gap: 8, alignItems: "center", justifyContent: "space-between", flexWrap: "wrap" }}>
          <SectionLabel>Périodes comptables</SectionLabel>
          {canManage && next.last && (
            <Button size="sm" variant={next.dueSoon ? "primary" : "subtle"} disabled={busy || !next.canOpen}
              title={next.canOpen ? undefined : `${next.last.name} n'a pas encore commencé.`}
              onClick={() => setDialog({ kind: "next", period: next.last! })}>Ouvrir l'exercice suivant</Button>
          )}
        </div>
        {canManage && next.dueSoon && next.last && (
          <div role="status" style={{ margin: "6px 0", fontSize: 13, color: "var(--nx-text-2)" }}>
            {next.last.name} se termine le {dateShort(next.last.endDate)} : ouvrez l'exercice suivant pour préparer le changement d'année.
          </div>
        )}
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <DataTable exportTitle="Périodes comptables"
          columns={[
            { key: "n", header: "Période", render: (p) => p.name },
            { key: "d", header: "Du", render: (p) => dateShort(p.startDate) },
            { key: "f", header: "Au", render: (p) => dateShort(p.endDate) },
            { key: "s", header: "Statut", render: (p) => <Badge tone={PERIOD_STATUS[p.status]?.tone}>{PERIOD_STATUS[p.status]?.label ?? p.status}</Badge> },
            { key: "a", header: "", render: (p) => (
              <div style={{ display: "flex", gap: 6, justifyContent: "flex-end" }}>
                {p.status === "open" && hasPermission("accounting.close_period") &&
                  <Button size="sm" variant="subtle" disabled={busy} onClick={() => setDialog({ kind: "close", period: p })}>Clôturer</Button>}
                {p.status === "closed" && hasPermission("accounting.reopen_period") &&
                  <Button size="sm" variant="ghost" disabled={busy} onClick={() => setDialog({ kind: "reopen", period: p })}>Rouvrir</Button>}
              </div>) },
          ]}
          rows={periods} rowKey={(p) => p.id} empty="Aucune période comptable." />
      </Card>

      <Card>
        <SectionLabel>Clôture d'exercice</SectionLabel>
        <Field label="Exercice">
          <Select value={selected?.id ?? ""} onChange={(e) => setPeriodId(e.target.value)}>
            {periods.map((p) => <option key={p.id} value={p.id}>{p.name} ({PERIOD_STATUS[p.status]?.label ?? p.status})</option>)}
          </Select>
        </Field>
        {report.error && <div role="alert" className="nx-error-text">{report.error}</div>}
        {r && (
          <div style={{ display: "grid", gap: 10, marginTop: 10 }}>
            <div style={{ display: "flex", gap: 16, flexWrap: "wrap" }}>
              <span>Produits : <strong>{money(r.revenue, r.currency)}</strong></span>
              <span>Charges : <strong>{money(r.expenses, r.currency)}</strong></span>
              <span>Résultat : <strong style={{ color: r.netResult < 0 ? "var(--nx-neg)" : undefined }}>{money(r.netResult, r.currency)}</strong></span>
            </div>
            <DataTable exportTitle={`Contrôles de clôture — ${r.periodName}`}
              columns={[
                { key: "l", header: "Contrôle", render: (c: YearEndControl) => <>{c.label} {c.blocking && <Badge>bloquant</Badge>}</> },
                { key: "s", header: "État", render: (c) => <Badge tone={CONTROL_TONE[c.status]}>{CONTROL_LABEL[c.status]}</Badge> },
                { key: "d", header: "Détail", render: (c) => c.detail ?? "—" },
              ]}
              rows={r.controls} rowKey={(c) => c.key} empty="—" />
            {hasPermission("accounting.carry_forward") && selected && (
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
                {r.status === "completed"
                  ? <Badge tone="success">Exercice clôturé</Badge>
                  : <Button disabled={busy || !r.canClose} onClick={() => setDialog({ kind: "year", period: selected })}>Clôturer l'exercice</Button>}
                <Button variant="subtle" disabled={busy} onClick={() => setDialog({ kind: "opening", period: selected })}>Générer les à-nouveaux</Button>
                {!r.canClose && r.status !== "completed" && <span style={{ fontSize: 12, color: "var(--nx-text-2)" }}>Levez les contrôles bloquants avant de clôturer.</span>}
              </div>
            )}
          </div>
        )}
      </Card>

      {dialog?.kind === "next" && (
        <ConfirmDialog title="Ouvrir l'exercice suivant" confirmLabel="Ouvrir l'exercice"
          message={`Un exercice d'un an commencera le lendemain de la fin de ${dialog.period.name} (${dateShort(dialog.period.endDate)}). Les opérations datées de cet exercice seront acceptées.`}
          onCancel={() => setDialog(null)}
          onConfirm={async () => {
            setDialog(null);
            const created = await submit(() => openNextYear());
            if (created) { toast.success(`${created.name} ouvert.`); done(); }
          }} />
      )}
      {dialog?.kind === "close" && (
        <ConfirmDialog title={`Clôturer ${dialog.period.name}`} confirmLabel="Clôturer"
          message="Plus aucune écriture ne pourra être datée dans cette période (ventes, achats et saisies manuelles comprises) tant qu'elle n'est pas rouverte."
          onCancel={() => setDialog(null)}
          onConfirm={async () => {
            const p = dialog.period; setDialog(null);
            if (await submit(() => closePeriod(p.id).then(() => true))) { toast.success(`${p.name} clôturée.`); done(); }
          }} />
      )}
      {dialog?.kind === "reopen" && (
        <ConfirmDialog title={`Rouvrir ${dialog.period.name}`} confirmLabel="Rouvrir" requireReason
          message="La réouverture est tracée avec son motif dans le journal d'audit."
          onCancel={() => setDialog(null)}
          onConfirm={async (reason) => {
            const p = dialog.period; setDialog(null);
            if (await submit(() => reopenPeriod(p.id, reason).then(() => true))) { toast.success(`${p.name} rouverte.`); done(); }
          }} />
      )}
      {dialog?.kind === "year" && r && (
        <ConfirmDialog title={`Clôturer l'exercice ${dialog.period.name}`} confirmLabel="Clôturer l'exercice" danger
          message="Les comptes de produits et de charges sont soldés et le résultat est porté en capitaux propres par une écriture de clôture. Opération définitive."
          impact={<>Résultat porté : <strong>{money(r.netResult, r.currency)}</strong></>}
          onCancel={() => setDialog(null)}
          onConfirm={async () => {
            const p = dialog.period; setDialog(null);
            const res = await submit(() => closeYear(p.id));
            if (res) { toast.success(`Exercice clôturé (écriture ${res.resultEntryNumber}).`); done(); }
          }} />
      )}
      {dialog?.kind === "opening" && (
        <OpeningBalancesDialog target={dialog.period} periods={periods} onClose={() => setDialog(null)}
          onDone={() => { setDialog(null); done(); }} />
      )}
    </div>
  );
}

function OpeningBalancesDialog({ target, periods, onClose, onDone }: {
  target: AccountingPeriod; periods: AccountingPeriod[]; onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const sources = periods.filter((p) => p.id !== target.id && p.endDate < target.startDate);
  const [source, setSource] = useState(sources[sources.length - 1]?.id ?? "");
  const { busy, error, submit } = useSubmit();

  async function run() {
    if (!source) return;
    const r = await submit(() => generateOpeningBalances(target.id, source));
    if (r) { toast.success(`À-nouveaux générés : écriture ${r.entryNumber} (${r.lineCount} lignes).`); onDone(); }
  }

  return (
    <Modal title={`À-nouveaux de ${target.name}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy || !source} onClick={run}>Générer</Button></>}>
      <div style={{ display: "grid", gap: 10, minWidth: "min(440px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <p style={{ margin: 0 }}>Les soldes des comptes de bilan de l'exercice source, clôturé, sont repris au premier jour de {target.name}.</p>
        {sources.length === 0
          ? <div className="nx-error-text">Aucun exercice antérieur à {target.name} : il n'y a pas de soldes à reprendre.</div>
          : (
            <Field label="Exercice source" required>
              <Select value={source} onChange={(e) => setSource(e.target.value)}>
                {sources.map((p) => <option key={p.id} value={p.id}>{p.name} ({PERIOD_STATUS[p.status]?.label ?? p.status})</option>)}
              </Select>
            </Field>
          )}
      </div>
    </Modal>
  );
}
