import { useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import { dateTime, todayIso } from "@/shared/format";
import { Button, Card, DataTable, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import { financeLookups, listExchangeRates, recordExchangeRate } from "@/finance/treasuryApi";

/**
 * Taux de change (audit 2026-10, A-017) : sans eux, aucune opération en devise étrangère (compte en USD, vente ou achat
 * en devise, consolidation) n'était possible sur une installation neuve. Un taux s'applique à toute l'installation.
 */
export function ExchangeRatesPanel() {
  const { hasPermission } = useAuth();
  const rates = useAsync(listExchangeRates, []);
  const [adding, setAdding] = useState(false);

  return (
    <Card>
      <SectionLabel action={hasPermission("finance.update") && <Button size="sm" onClick={() => setAdding(true)}>Nouveau taux</Button>}>
        Taux de change
      </SectionLabel>
      <p style={{ margin: "0 0 8px", fontSize: 13, color: "var(--nx-text-2)" }}>
        Un taux s'applique à partir de sa date d'effet ; les opérations passées gardent le taux de leur date. Pour changer
        un taux, saisissez-en un nouveau.
      </p>
      {rates.error && <div role="alert" className="nx-error-text">{rates.error}</div>}
      <DataTable exportTitle="Taux de change"
        columns={[
          { key: "p", header: "Paire", render: (r) => `1 ${r.baseCurrency} = … ${r.quoteCurrency}` },
          { key: "r", header: "Taux", numeric: true, render: (r) => r.rate.toLocaleString("fr-FR", { maximumFractionDigits: 6 }) },
          { key: "e", header: "En vigueur depuis", render: (r) => dateTime(r.effectiveAt) },
          { key: "s", header: "Source", render: (r) => (r.source === "manual" ? "Saisie" : r.source) },
        ]}
        rows={rates.data ?? []} rowKey={(r) => r.id} empty={rates.loading ? "Chargement…" : "Aucun taux saisi."} />
      {adding && <RateDialog onClose={() => setAdding(false)} onSaved={() => { setAdding(false); rates.reload(); }} />}
    </Card>
  );
}

function RateDialog({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const lookups = useAsync(financeLookups, []);
  const currencies = lookups.data?.currencies ?? [];
  const company = currencies.find((c) => c.isCompanyDefault)?.code ?? "HTG";
  const [base, setBase] = useState("USD");
  const [quote, setQuote] = useState(company);
  const [rate, setRate] = useState("");
  const [date, setDate] = useState(todayIso());
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "finance.rate_currency": "base", "finance.rate_value": "rate", "finance.rate_date": "date", "finance.rate_exists": "date",
  });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const value = num(rate);
    if (value === null || value <= 0) return fail("rate", "Taux strictement positif.");
    if (base === quote) return fail("base", "Choisissez deux devises différentes.");
    // Aujourd'hui : en vigueur immédiatement ; autre jour : midi UTC, qui reste ce jour-là dans tous les fuseaux.
    const effectiveAt = date === todayIso() ? null : `${date}T12:00:00Z`;
    const ok = await submit(async () => { await recordExchangeRate({ baseCurrency: base, quoteCurrency: quote, rate: value, effectiveAt }); return true; });
    if (ok) { toast.success(`Taux enregistré : 1 ${base} = ${value} ${quote}.`); onSaved(); }
  }

  return (
    <Modal title="Nouveau taux de change" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(420px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="1 unité de" error={fieldErrors.base}>
          <Select value={base} onChange={(e) => setBase(e.target.value)}>
            {currencies.map((c) => <option key={c.code} value={c.code}>{c.code} — {c.name}</option>)}
          </Select>
        </Field>
        <Field label="vaut (en)">
          <Select value={quote} onChange={(e) => setQuote(e.target.value)}>
            {currencies.map((c) => <option key={c.code} value={c.code}>{c.code} — {c.name}</option>)}
          </Select>
        </Field>
        <Field label="Taux" required error={fieldErrors.rate}>
          <TextInput inputMode="decimal" value={rate} onChange={(e) => setRate(e.target.value)} placeholder="ex. 132,50" />
        </Field>
        <Field label="Date d'effet" error={fieldErrors.date}>
          <TextInput type="date" value={date} onChange={(e) => setDate(e.target.value)} />
        </Field>
      </form>
    </Modal>
  );
}
