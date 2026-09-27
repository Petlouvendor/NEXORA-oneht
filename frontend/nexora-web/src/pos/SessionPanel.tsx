import { useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { Badge, Button, Card, Field, Modal, Select, TextInput } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { dateTime, money } from "@/shared/format";
import { ManagerOverrideDialog } from "@/pos/ManagerOverrideDialog";
import { useToast } from "@/shared/Toast";
import {
  closeSession, listRegisters, openSession,
  type CashRegister, type CurrentSession, type SessionSummary,
} from "@/pos/posApi";

/**
 * Session de caisse (guide §13) : ouverture avec fond de caisse, clôture par comptage. Le montant
 * attendu n'est pas affiché avant le comptage ; l'écart est calculé par le serveur. Au-delà du seuil
 * configuré, la caisse passe « en attente de validation Manager » et ne vend plus.
 */
export function SessionPanel({ session, onChanged }: { session: CurrentSession | null; onChanged: () => void }) {
  const toast = useToast();
  const [registers, setRegisters] = useState<CashRegister[]>([]);
  const [registerId, setRegisterId] = useState("");
  const [opening, setOpening] = useState("0");
  const [closing, setClosing] = useState(false);
  const [counted, setCounted] = useState("");
  const [notes, setNotes] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [summary, setSummary] = useState<SessionSummary | null>(null);
  const [onSite, setOnSite] = useState(false);

  useEffect(() => {
    if (session) return;
    listRegisters()
      .then((r) => {
        setRegisters(r);
        setRegisterId((current) => current || r.find((x) => !x.hasOpenSession)?.id || "");
      })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Caisses indisponibles."));
  }, [session]);

  async function open() {
    setBusy(true);
    setError(null);
    try {
      await openSession(registerId, Number(opening) || 0);
      toast.success("Session de caisse ouverte.");
      onChanged();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Ouverture impossible.");
    } finally {
      setBusy(false);
    }
  }

  async function close() {
    if (!session) return;
    setBusy(true);
    setError(null);
    try {
      const result = await closeSession(session.sessionId, Number(counted), notes.trim() || null);
      setSummary(result);
      setClosing(false);
      setCounted("");
      setNotes("");
      onChanged();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Clôture impossible.");
    } finally {
      setBusy(false);
    }
  }

  if (!session) {
    const free = registers.filter((r) => !r.hasOpenSession);
    return (
      <Card className="nx-field" style={{ gap: 10 }}>
        <div style={{ fontWeight: 700 }}><Icon.Wallet size={16} /> Ouvrir une session de caisse</div>
        <Field label="Caisse" required>
          <Select value={registerId} onChange={(e) => setRegisterId(e.target.value)}>
            <option value="">Choisir…</option>
            {registers.map((r) => (
              <option key={r.id} value={r.id} disabled={r.hasOpenSession}>
                {r.name} ({r.currency}){r.hasOpenSession ? ` — occupée par ${r.openedBy ?? "une autre session"}` : ""}
              </option>
            ))}
          </Select>
        </Field>
        {registers.length === 0 && !error && (
          <div style={{ color: "var(--nx-warning)", fontSize: "var(--nx-fs-sm)" }}>
            <Icon.Alert size={14} /> Aucune caisse n'est configurée pour cette entreprise : contactez l'administrateur.
          </div>
        )}
        {registers.length > 0 && free.length === 0 && (
          <div style={{ color: "var(--nx-warning)", fontSize: "var(--nx-fs-sm)" }} role="status">
            <Icon.Alert size={14} /> Toutes les caisses sont occupées
            ({registers.map((r) => `${r.name} : ${r.openedBy ?? "session ouverte"}`).join(", ")}).
            Une caisse n'accepte qu'une session à la fois : demandez à cette personne ou à un responsable
            de clôturer la session (Ventes / POS → Validations → Sessions ouvertes).
          </div>
        )}
        <Field label="Fond de caisse initial" required>
          <TextInput type="number" min={0} inputMode="decimal" value={opening} onChange={(e) => setOpening(e.target.value)} />
        </Field>
        {error && <div className="nx-error-text" role="alert">{error}</div>}
        <Button loading={busy} disabled={busy || !registerId} onClick={() => void open()}>Ouvrir la caisse</Button>
        {summary && <ClosingResult summary={summary} onClose={() => setSummary(null)} />}
      </Card>
    );
  }

  return (
    <Card style={{ display: "grid", gap: 8 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 8 }}>
        <div>
          <div style={{ fontWeight: 700 }}>{session.registerName}</div>
          <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-xs)" }}>
            Ouverte {dateTime(session.openedAt)} · {session.saleCount} vente(s)
          </div>
        </div>
        {session.status === "closing"
          ? <Badge tone="warning">En attente de validation</Badge>
          : <Button size="sm" variant="ghost" onClick={() => setClosing(true)}>Clôturer</Button>}
      </div>
      {session.status === "closing" && (
        <>
          <div style={{ color: "var(--nx-warning)", fontSize: "var(--nx-fs-sm)" }}>
            <Icon.Alert size={14} /> Écart de caisse au-delà du seuil : un Manager doit valider la clôture. Les ventes sont suspendues.
          </div>
          <Button size="sm" onClick={() => setOnSite(true)}>Responsable présent : valider la clôture</Button>
        </>
      )}
      {onSite && (
        <ManagerOverrideDialog
          title="Validation responsable — écart de caisse"
          summary={<span>Clôture de <b>{session.registerName}</b> en écart au-delà du seuil. Le responsable confirme le
            comptage : l'écart est alors passé en comptabilité et la caisse est clôturée.</span>}
          approveAction="validate_close" targetId={session.sessionId} noteRequiredToApprove
          onClose={() => setOnSite(false)}
          onDone={() => { setOnSite(false); onChanged(); }}
        />
      )}
      {closing && (
        <Modal title="Clôture de caisse" onClose={() => setClosing(false)}
               footer={<>
                 <Button variant="ghost" onClick={() => setClosing(false)}>Annuler</Button>
                 <Button loading={busy} disabled={busy || counted === "" || Number(counted) < 0} onClick={() => void close()}>
                   Valider le comptage
                 </Button>
               </>}>
          <div style={{ display: "grid", gap: 10 }}>
            <p style={{ margin: 0, color: "var(--nx-text-2)" }}>
              Comptez l'argent présent dans le tiroir et saisissez le total. Le montant attendu et l'écart sont
              calculés par le serveur.
            </p>
            <Field label={`Montant compté (${session.currency})`} required>
              <TextInput type="number" min={0} inputMode="decimal" autoFocus value={counted} onChange={(e) => setCounted(e.target.value)} />
            </Field>
            <Field label="Commentaire">
              <TextInput value={notes} onChange={(e) => setNotes(e.target.value)} />
            </Field>
            {error && <div className="nx-error-text" role="alert">{error}</div>}
          </div>
        </Modal>
      )}
      {summary && <ClosingResult summary={summary} currency={session.currency} onClose={() => setSummary(null)} />}
    </Card>
  );
}

function ClosingResult({ summary, currency = "HTG", onClose }: { summary: SessionSummary; currency?: string; onClose: () => void }) {
  const diff = summary.difference ?? 0;
  return (
    <Modal title={summary.status === "closed" ? "Caisse clôturée" : "Clôture en attente de validation"} onClose={onClose}
           footer={<Button onClick={onClose}>Fermer</Button>}>
      <div style={{ display: "grid", gap: 6 }}>
        <Row label="Fond initial" value={money(summary.openingBalance, currency)} />
        <Row label="Attendu" value={money(summary.expectedBalance, currency)} />
        <Row label="Compté" value={money(summary.countedBalance ?? 0, currency)} />
        <Row label="Écart" value={money(diff, currency, { sign: true })} strong tone={diff === 0 ? undefined : "var(--nx-warning)"} />
        {summary.status === "closing" && (
          <p style={{ margin: "8px 0 0", color: "var(--nx-warning)" }}>
            L'écart dépasse le seuil autorisé : un Manager a été notifié et doit valider la clôture.
          </p>
        )}
      </div>
    </Modal>
  );
}

function Row({ label, value, strong, tone }: { label: string; value: string; strong?: boolean; tone?: string }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", fontWeight: strong ? 700 : 400, color: tone }}>
      <span>{label}</span><span className="nx-numeric">{value}</span>
    </div>
  );
}
