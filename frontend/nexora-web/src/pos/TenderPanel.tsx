import { Button, IconButton, Select, TextInput } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { money } from "@/shared/format";
import {
  isCashLine, METHOD_ICON, newTenderLine, quickCashAmounts, type TenderLine, type TenderMethodOption, type TenderSummary,
} from "@/pos/tenders";

/**
 * Saisie des encaissements (phase 5) : une ligne par moyen (espèces, carte, mobile money…), montants rapides
 * pour les espèces, référence exigée quand le moyen l'impose, compte choisi s'il y en a plusieurs. Montant
 * vide partout = paiement exact en espèces.
 */
export function TenderPanel({ lines, onChange, methods, due, currency, summary, disabled }: {
  lines: TenderLine[]; onChange: (lines: TenderLine[]) => void; methods: TenderMethodOption[]; due: number;
  currency: string; summary: TenderSummary; disabled?: boolean;
}) {
  const set = (key: string, patch: Partial<TenderLine>) => onChange(lines.map((l) => (l.key === key ? { ...l, ...patch } : l)));
  const cashLine = lines.find((l) => isCashLine(l, methods));
  // Espèces nécessaires = ce qui reste une fois les autres moyens comptés.
  const cashNeeded = Math.max(0, Math.round((due - summary.nonCash) * 100) / 100);
  const others = methods.filter((m) => m.type !== "cash");

  function add(method: TenderMethodOption) {
    const line = newTenderLine(method);
    onChange([...lines, { ...line, amount: summary.remaining > 0 ? String(summary.remaining) : "" }]);
  }

  return (
    <div style={{ display: "grid", gap: 8 }}>
      {lines.map((l) => {
        const method = methods.find((m) => m.id === l.methodId) ?? null;
        const cash = isCashLine(l, methods);
        const label = method?.name ?? "Espèces";
        const missingRef = summary.missingReference.includes(l.key);
        const missingAcc = summary.missingAccount.includes(l.key);
        return (
          <div key={l.key} style={{ display: "grid", gap: 4, padding: 8, border: "1px solid var(--nx-border)", borderRadius: 8 }}>
            <div style={{ display: "flex", gap: 6, alignItems: "center" }}>
              <b style={{ flex: 1 }}>{label}{method && method.name !== METHOD_ICON[method.type] ? <span style={{ color: "var(--nx-text-2)", fontWeight: 400 }}> · {METHOD_ICON[method.type] ?? method.type}</span> : null}</b>
              {lines.length > 1 && (
                <IconButton label={`Retirer le paiement ${label}`} disabled={disabled} onClick={() => onChange(lines.filter((x) => x.key !== l.key))}>
                  <Icon.X size={14} />
                </IconButton>
              )}
            </div>
            <TextInput type="number" min={0} step="0.01" className="nx-input nx-numeric" value={l.amount} disabled={disabled}
                       aria-label={`Montant ${label}`} placeholder={cash && lines.length === 1 ? `${money(due, currency)} (exact)` : "0"}
                       style={{ fontSize: "var(--nx-fs-lg)", textAlign: "right" }}
                       onChange={(e) => set(l.key, { amount: e.target.value })} />
            {!cash && method && method.accounts.length > 1 && (
              <Select value={l.accountId} aria-label={`Compte d'encaissement ${label}`} disabled={disabled}
                      aria-invalid={missingAcc || undefined} onChange={(e) => set(l.key, { accountId: e.target.value })}>
                <option value="">Encaissé sur…</option>
                {method.accounts.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
              </Select>
            )}
            {!cash && method && method.accounts.length === 0 && (
              <div className="nx-err">Aucun compte {currency} configuré pour ce moyen.</div>
            )}
            {!cash && (
              <TextInput value={l.reference} disabled={disabled} aria-label={`Référence ${label}`} invalid={missingRef}
                         placeholder={method?.requiresReference ? "Référence obligatoire (n° d'autorisation, de transaction)" : "Référence (facultatif)"}
                         onChange={(e) => set(l.key, { reference: e.target.value })} />
            )}
            {missingAcc && <div className="nx-err">Choisissez le compte d'encaissement.</div>}
          </div>
        );
      })}

      {cashLine && cashNeeded > 0 && (
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }} aria-label="Montants rapides en espèces">
          {quickCashAmounts(cashNeeded).map((v, i) => (
            <Button key={v} size="sm" variant={i === 0 ? "subtle" : "ghost"} disabled={disabled}
                    onClick={() => set(cashLine.key, { amount: String(v) })}>
              {i === 0 ? "Exact" : money(v, currency)}
            </Button>
          ))}
        </div>
      )}

      {others.length > 0 && (
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap", alignItems: "center" }}>
          <span style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>Ajouter :</span>
          {others.map((m) => (
            <Button key={m.id} size="sm" variant="ghost" disabled={disabled} onClick={() => add(m)}>+ {m.name}</Button>
          ))}
          {!cashLine && methods.some((m) => m.type === "cash") && (
            <Button size="sm" variant="ghost" disabled={disabled} onClick={() => add(methods.find((m) => m.type === "cash")!)}>+ Espèces</Button>
          )}
        </div>
      )}
    </div>
  );
}
