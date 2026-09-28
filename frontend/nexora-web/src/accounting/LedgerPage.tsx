import { todayIso } from "@/shared/format";
import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  Badge, Button, Card, DataTable, Drawer, KpiCard, Money, SectionLabel, StatePill, Tabs,
  type Column,
} from "@/shared/ui";
import {
  closeYear, createDraft, deleteDraft, generateOpeningBalances, getTrialBalance, listEntries,
  listJournals, listPeriods, postEntry, previewCloseYear, reverseEntry,
  type AccountingPeriod, type Journal, type JournalEntry, type TrialBalance, type YearEndCloseReport,
} from "@/accounting/accountingApi";

interface DraftRow { accountCode: string; debit: string; credit: string }

/** Comptabilité générale (Phase 29 §9-19, §38-41) : écritures manuelles, balance, clôture d'exercice. */
export function LedgerPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("entries");
  const [entries, setEntries] = useState<JournalEntry[]>([]);
  const [journals, setJournals] = useState<Journal[]>([]);
  const [periods, setPeriods] = useState<AccountingPeriod[]>([]);
  const [trial, setTrial] = useState<TrialBalance | null>(null);
  const [periodId, setPeriodId] = useState("");
  const [close, setClose] = useState<YearEndCloseReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [drawer, setDrawer] = useState(false);

  const [jrn, setJrn] = useState("OD");
  const [date, setDate] = useState(todayIso());
  const [desc, setDesc] = useState("");
  const [rows, setRows] = useState<DraftRow[]>([
    { accountCode: "", debit: "", credit: "" },
    { accountCode: "", debit: "", credit: "" },
  ]);

  const canCreate = hasPermission("accounting.create");
  const canPost = hasPermission("accounting.post");
  const canReverse = hasPermission("accounting.reverse");
  const canCarry = hasPermission("accounting.carry_forward");

  const load = useCallback(() => {
    setError(null);
    Promise.all([
      listEntries().catch(() => [] as JournalEntry[]),
      listJournals().catch(() => [] as Journal[]),
      listPeriods().catch(() => [] as AccountingPeriod[]),
      getTrialBalance(todayIso()).catch(() => null),
    ])
      .then(([e, j, p, t]) => {
        setEntries(e); setJournals(j); setPeriods(p); setTrial(t);
        if (p.length && !periodId) setPeriodId(p[p.length - 1].id);
      })
      .catch((x) => setError(x instanceof ApiError ? x.message : "Erreur de chargement"));
  }, [periodId]);

  useEffect(load, [load]);

  useEffect(() => {
    if (!periodId) return;
    previewCloseYear(periodId).then(setClose).catch(() => setClose(null));
  }, [periodId, entries]);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true); setError(null);
    try { await fn(); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Action refusée"); }
    finally { setBusy(false); }
  }

  const draftTotals = useMemo(() => {
    const d = rows.reduce((s, r) => s + (Number(r.debit) || 0), 0);
    const c = rows.reduce((s, r) => s + (Number(r.credit) || 0), 0);
    return { d, c, balanced: d > 0 && Math.round(d * 100) === Math.round(c * 100) };
  }, [rows]);

  async function saveDraft(thenPost: boolean) {
    const lines = rows
      .filter((r) => r.accountCode && (Number(r.debit) || Number(r.credit)))
      .map((r) => ({ accountCode: r.accountCode.trim(), debit: Number(r.debit) || 0, credit: Number(r.credit) || 0 }));
    await run(async () => {
      const id = await createDraft({ journalCode: jrn, entryDate: date, currency: "HTG", description: desc || null, lines });
      if (thenPost) await postEntry(id);
      setDrawer(false);
      setRows([{ accountCode: "", debit: "", credit: "" }, { accountCode: "", debit: "", credit: "" }]);
      setDesc("");
    });
  }

  if (!hasPermission("accounting.view")) {
    return <Card>La comptabilité n'est pas accessible avec vos permissions.</Card>;
  }

  const entryCols: Array<Column<JournalEntry>> = [
    { key: "d", header: "Date", render: (e) => e.entryDate },
    { key: "j", header: "Journal", render: (e) => <Badge>{e.journalCode}</Badge> },
    { key: "n", header: "N°", render: (e) => e.entryNumber },
    { key: "l", header: "Libellé", render: (e) => e.description ?? "—" },
    { key: "o", header: "Origine", render: (e) => e.origin },
    { key: "m", header: "Montant", numeric: true, render: (e) => <Money value={e.totalDebit} currency={e.currency} /> },
    { key: "s", header: "Statut", render: (e) => <StatePill status={e.status} /> },
    {
      key: "a", header: "", render: (e) => (
        <div style={{ display: "flex", gap: 6, justifyContent: "flex-end" }}>
          {e.status === "draft" && canPost && (
            <Button size="sm" variant="subtle" disabled={busy} onClick={() => run(() => postEntry(e.id))}>Valider</Button>
          )}
          {e.status === "draft" && canCreate && (
            <Button size="sm" variant="danger" disabled={busy} onClick={() => run(() => deleteDraft(e.id))}>Suppr.</Button>
          )}
          {e.status === "posted" && canReverse && (
            <Button size="sm" variant="subtle" disabled={busy}
              onClick={() => { const r = window.prompt("Motif de contrepassation ?"); if (r) run(() => reverseEntry(e.id, r)); }}>
              Contrepasser
            </Button>
          )}
        </div>
      ),
    },
  ];

  const trialCols: Array<Column<TrialBalance["rows"][number]>> = [
    { key: "c", header: "Compte", render: (r) => `${r.accountCode} ${r.accountName}` },
    { key: "t", header: "Type", render: (r) => <Badge>{r.accountType}</Badge> },
    { key: "d", header: "Débit", numeric: true, render: (r) => <Money value={r.debit} currency={trial?.currency ?? "HTG"} /> },
    { key: "cr", header: "Crédit", numeric: true, render: (r) => <Money value={r.credit} currency={trial?.currency ?? "HTG"} /> },
  ];

  const result = close?.netResult ?? 0;

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Comptabilité générale</h1>
      {error && <div className="nx-error-text">{error}</div>}

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 12 }}>
        <KpiCard label="Résultat de l'exercice" value={<Money value={result} currency={close?.currency ?? "HTG"} />} tone={result < 0 ? "warning" : undefined} />
        <KpiCard label="Produits" value={<Money value={close?.revenue ?? 0} currency={close?.currency ?? "HTG"} />} />
        <KpiCard label="Charges" value={<Money value={close?.expenses ?? 0} currency={close?.currency ?? "HTG"} />} />
        <KpiCard label="Balance équilibrée" value={trial && trial.totalDebit === trial.totalCredit ? "Oui" : "—"} />
        <KpiCard label="Brouillons" value={String(entries.filter((e) => e.status === "draft").length)} tag={entries.some((e) => e.status === "draft") ? "!" : undefined} />
      </div>

      <Card>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <Tabs
            tabs={[
              { id: "entries", label: `Écritures (${entries.length})` },
              { id: "balance", label: "Balance" },
              { id: "close", label: "Clôture d'exercice" },
            ]}
            active={tab}
            onChange={setTab}
          />
          {tab === "entries" && canCreate && (
            <Button size="sm" disabled={busy} onClick={() => setDrawer(true)}>Nouvelle écriture</Button>
          )}
        </div>

        <div style={{ marginTop: 12 }}>
          {tab === "entries" && <DataTable columns={entryCols} rows={entries} rowKey={(e) => e.id} empty="Aucune écriture." />}
          {tab === "balance" && (
            <>
              <div style={{ fontSize: 12, color: "var(--nx-text-2)", marginBottom: 8 }}>
                Au {trial?.asOf} — Débit <Money value={trial?.totalDebit ?? 0} currency={trial?.currency ?? "HTG"} /> /
                Crédit <Money value={trial?.totalCredit ?? 0} currency={trial?.currency ?? "HTG"} />
              </div>
              <DataTable columns={trialCols} rows={trial?.rows ?? []} rowKey={(r) => r.accountCode} empty="—" />
            </>
          )}
          {tab === "close" && (
            <div style={{ display: "grid", gap: 12 }}>
              <label style={{ display: "flex", gap: 8, alignItems: "center", fontSize: 13 }}>
                Exercice
                <select value={periodId} onChange={(e) => setPeriodId(e.target.value)}>
                  {periods.map((p) => <option key={p.id} value={p.id}>{p.name} ({p.status})</option>)}
                </select>
              </label>
              {close && (
                <>
                  <DataTable
                    columns={[
                      { key: "l", header: "Contrôle", render: (c: YearEndCloseReport["controls"][number]) => <>{c.label} {c.blocking && <Badge>bloquant</Badge>}</> },
                      { key: "s", header: "État", render: (c) => <Badge tone={c.status === "pass" ? "success" : c.status === "warn" ? "warning" : "error"}>{c.status}</Badge> },
                      { key: "d", header: "Détail", render: (c) => c.detail ?? "—" },
                    ] as Array<Column<YearEndCloseReport["controls"][number]>>}
                    rows={close.controls}
                    rowKey={(c) => c.key}
                    empty="—"
                  />
                  <div style={{ fontSize: 13 }}>
                    Résultat net : <strong><Money value={close.netResult} currency={close.currency} /></strong>
                  </div>
                  {canCarry && (
                    <div style={{ display: "flex", gap: 8 }}>
                      {close.status === "completed"
                        ? <Badge tone="success">Exercice clôturé</Badge>
                        : (
                          <Button disabled={busy || !close.canClose} onClick={() => run(() => closeYear(periodId))}>
                            Clôturer l'exercice
                          </Button>
                        )}
                      <Button variant="subtle" disabled={busy}
                        onClick={() => {
                          const from = window.prompt("Nom de l'exercice source pour les à-nouveaux ?");
                          const src = periods.find((p) => p.name === from);
                          if (src) run(() => generateOpeningBalances(periodId, src.id));
                          else if (from) setError("Exercice source introuvable.");
                        }}>
                        Générer les à-nouveaux
                      </Button>
                    </div>
                  )}
                </>
              )}
            </div>
          )}
        </div>
      </Card>

      {drawer && (
      <Drawer title="Nouvelle écriture (brouillon)" onClose={() => setDrawer(false)}>
        <div style={{ display: "grid", gap: 10 }}>
          <label style={{ fontSize: 13 }}>Journal{" "}
            <select value={jrn} onChange={(e) => setJrn(e.target.value)}>
              {journals.map((j) => <option key={j.code} value={j.code}>{j.code} — {j.name}</option>)}
            </select>
          </label>
          <label style={{ fontSize: 13 }}>Date{" "}
            <input type="date" value={date} onChange={(e) => setDate(e.target.value)} />
          </label>
          <label style={{ fontSize: 13 }}>Libellé{" "}
            <input value={desc} onChange={(e) => setDesc(e.target.value)} placeholder="Opération diverse" />
          </label>
          <SectionLabel>Lignes</SectionLabel>
          {rows.map((r, i) => (
            <div key={i} style={{ display: "grid", gridTemplateColumns: "1fr 90px 90px", gap: 6 }}>
              <input placeholder="Compte" value={r.accountCode}
                onChange={(e) => setRows((p) => p.map((x, j) => j === i ? { ...x, accountCode: e.target.value } : x))} />
              <input placeholder="Débit" inputMode="decimal" value={r.debit}
                onChange={(e) => setRows((p) => p.map((x, j) => j === i ? { ...x, debit: e.target.value } : x))} />
              <input placeholder="Crédit" inputMode="decimal" value={r.credit}
                onChange={(e) => setRows((p) => p.map((x, j) => j === i ? { ...x, credit: e.target.value } : x))} />
            </div>
          ))}
          <Button size="sm" variant="subtle" onClick={() => setRows((p) => [...p, { accountCode: "", debit: "", credit: "" }])}>
            + Ligne
          </Button>
          <div style={{ fontSize: 12, color: draftTotals.balanced ? "var(--nx-text-2)" : "var(--nx-warning, #b45309)" }}>
            Débit {draftTotals.d} / Crédit {draftTotals.c} — {draftTotals.balanced ? "équilibré" : "déséquilibré"}
          </div>
          <div style={{ display: "flex", gap: 8 }}>
            <Button variant="subtle" disabled={busy} onClick={() => saveDraft(false)}>Enregistrer le brouillon</Button>
            {canPost && (
              <Button disabled={busy || !draftTotals.balanced} onClick={() => saveDraft(true)}>Enregistrer & valider</Button>
            )}
          </div>
        </div>
      </Drawer>
      )}
    </div>
  );
}
