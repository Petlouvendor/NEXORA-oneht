import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { Badge, Button, Card, IconButton, TextInput } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { money } from "@/shared/format";
import { listCredits, lookupCustomers, type CustomerCredit, type CustomerLookup } from "@/customers/customersApi";
import { PaymentDialog, errorText } from "@/customers/CustomerDialogs";
import { customerStatement } from "@/shared/printApi";
import { useToast } from "@/shared/Toast";

/**
 * Client de la vente (module clients §17, §19) : recherche progressive dès 2 caractères, 8 résultats au
 * plus, champs strictement utiles (code, nom, 4 derniers chiffres du téléphone, crédit disponible si le
 * rôle y a droit). Jamais la base clients entière dans le navigateur. « Versement » n'apparaît qu'avec
 * `credit.receive_payment` et encaisse sur la caisse de la session ouverte.
 */
export function CustomerPicker({ customer, onChange, disabled, cashAccountId, cashSessionId }: {
  customer: CustomerLookup | null; onChange: (c: CustomerLookup | null) => void; disabled?: boolean;
  cashAccountId?: string | null; cashSessionId?: string | null;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [printing, setPrinting] = useState(false);
  const [q, setQ] = useState("");
  const [hits, setHits] = useState<CustomerLookup[]>([]);
  const [open, setOpen] = useState(false);
  const [paying, setPaying] = useState<CustomerCredit[] | null>(null);
  const seq = useRef(0);

  useEffect(() => {
    const term = q.trim();
    if (term.length < 2) { setHits([]); return; }
    const mine = ++seq.current;
    const t = setTimeout(() => {
      lookupCustomers(term).then((r) => { if (mine === seq.current) { setHits(r); setOpen(true); } }).catch(() => setHits([]));
    }, 250);
    return () => clearTimeout(t);
  }, [q]);

  if (customer) {
    const warn = customer.status !== "active";
    return (
      <Card style={{ display: "grid", gap: 6, padding: 12 }}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 8 }}>
          <div>
            <div style={{ fontWeight: 700 }}>{customer.displayName}</div>
            <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
              {customer.customerNumber}{customer.phoneHint ? ` · tél. …${customer.phoneHint}` : ""}
            </div>
          </div>
          <IconButton label="Retirer le client" onClick={() => onChange(null)} disabled={disabled}><Icon.X size={14} /></IconButton>
        </div>
        {warn && <Badge tone="error">{customer.status === "blocked" ? "Client bloqué" : "Client inactif"} — pas de crédit</Badge>}
        {customer.availableStoreCredit != null && customer.availableStoreCredit > 0 && (
          <div style={{ fontSize: "var(--nx-fs-sm)" }}>Avoir disponible : <b className="nx-numeric">{money(customer.availableStoreCredit, customer.currency)}</b></div>
        )}
        {!warn && (customer.creditEnabled
          ? customer.availableCredit != null && <div style={{ fontSize: "var(--nx-fs-sm)" }}>Crédit disponible : <b className="nx-numeric">{money(customer.availableCredit, customer.currency)}</b></div>
          : <div style={{ fontSize: "var(--nx-fs-sm)", color: "var(--nx-text-2)" }}>Client au comptant (aucune limite de crédit).</div>)}
        {((hasPermission("credit.receive_payment") && cashAccountId) || hasPermission("credit.view_portfolio")) && (
          <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
            {hasPermission("credit.receive_payment") && cashAccountId && (
              <Button size="sm" variant="ghost" disabled={disabled}
                      onClick={() => listCredits(customer.id).then(setPaying).catch(() => setPaying([]))}>
                <Icon.Wallet size={14} /> Versement sur son compte
              </Button>
            )}
            {/* Relevé sur l'imprimante de caisse, comme un reçu (reste à verser, derniers mouvements). */}
            {hasPermission("credit.view_portfolio") && (
              <Button size="sm" variant="ghost" loading={printing}
                      onClick={() => {
                        setPrinting(true);
                        customerStatement(customer.id, "ticket", {}, true)
                          .catch((e) => toast.error(errorText(e, "Impression impossible.")))
                          .finally(() => setPrinting(false));
                      }}>
                <Icon.Printer size={14} /> Relevé (ticket)
              </Button>
            )}
          </div>
        )}
        {paying && (
          <PaymentDialog customerId={customer.id} customerName={customer.displayName} currency={customer.currency} credits={paying}
                         cashAccountId={cashAccountId} cashSessionId={cashSessionId}
                         onClose={() => setPaying(null)}
                         onDone={() => {
                           setPaying(null);
                           // Le crédit disponible a changé : on relit la fiche courte (jamais recalculé ici).
                           lookupCustomers(customer.customerNumber, 1).then((r) => r[0] && onChange(r[0])).catch(() => {});
                         }} />
        )}
      </Card>
    );
  }

  return (
    <div style={{ position: "relative" }}>
      <TextInput value={q} placeholder="Client : nom, code, téléphone…" aria-label="Rechercher un client" disabled={disabled}
                 onChange={(e) => setQ(e.target.value)} onFocus={() => hits.length && setOpen(true)} />
      {open && hits.length > 0 && (
        <div className="nx-card" role="listbox"
             style={{ position: "absolute", zIndex: 20, left: 0, right: 0, top: "100%", marginTop: 4, padding: 4, maxHeight: 280, overflowY: "auto" }}>
          {hits.map((h) => (
            <button key={h.id} role="option" aria-selected={false} type="button"
                    style={{ display: "block", width: "100%", textAlign: "left", background: "transparent", border: 0, padding: "8px 10px", cursor: "pointer", color: "inherit", font: "inherit" }}
                    onClick={() => { onChange(h); setQ(""); setHits([]); setOpen(false); }}>
              <b>{h.displayName}</b> <span style={{ color: "var(--nx-text-2)" }}>{h.customerNumber}{h.phoneHint ? ` · …${h.phoneHint}` : ""}</span>
              {h.status !== "active" && <Badge tone="error" style={{ marginLeft: 6 }}>{h.status === "blocked" ? "bloqué" : "inactif"}</Badge>}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
